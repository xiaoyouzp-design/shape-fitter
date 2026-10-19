"""Offline anime face landmarks using pinned hysts YOLOv3 + HRNetV2 weights."""
import argparse
from importlib.metadata import version
import json
import math
import os
from pathlib import Path
import sys
import time

from photo_auto_landmarks import sha


def json_safe(value):
    """Retain invalid raw outputs as null while keeping the result valid JSON."""
    if isinstance(value, float) and not math.isfinite(value):
        return None
    if isinstance(value, dict):
        return {key: json_safe(item) for key, item in value.items()}
    if isinstance(value, list):
        return [json_safe(item) for item in value]
    return value


def map_points(keypoints, mapping, width, height, threshold):
    if len(keypoints) != 28:
        raise ValueError("检测器没有返回预期的 28 个点。")
    if mapping.get("schema") != 1 or sorted(mapping["points"].values()) != list(range(28)):
        raise ValueError("二次元图片语义映射无效。")
    points, excluded = [], []
    for name, index in mapping["points"].items():
        px, py, score = (float(v) for v in keypoints[index])
        if not all(math.isfinite(v) for v in (px, py, score)):
            excluded.append(dict(id=name, reason="nonfinite"))
        elif score < threshold:
            excluded.append(dict(id=name, reason="low_heatmap_response", heatmapScore=score))
        elif not (0 <= px <= width - 1 and 0 <= py <= height - 1):
            excluded.append(dict(id=name, reason="outside_image", heatmapScore=score))
        else:
            points.append(dict(id=name, state="marked", x=px/(width-1), y=py/(height-1),
                               sourceIndex=index, heatmapScore=score))
    return points, excluded


def local_models(manifest_path, manifest):
    if manifest.get("schema") != 1 or manifest.get("engine") != "hysts-anime-face-detector":
        raise ValueError("二次元引擎配置无效，请指定 photo_auto_models/anime/engine.json。")
    result = {}
    for kind in ("face", "landmark"):
        item = manifest["models"][kind]
        path = (Path(manifest_path).resolve().parent / item["file"]).resolve()
        if not path.is_file() or path.suffix != ".safetensors":
            raise ValueError(f"缺少本地 safetensors 模型：{path}。请运行 setup_anime_models.py 安装作者发布的固定版本。")
        if sha(path) != item["sha256"]:
            raise ValueError(f"本地模型校验失败：{kind}，请重新安装模型。")
        result[kind] = path
    return result


def detect(args):
    started = time.perf_counter()
    manifest_path = Path(args.model)
    if not manifest_path.is_file():
        raise ValueError("缺少二次元引擎配置。请指定本地 photo_auto_models/anime/engine.json；运行 setup_anime_models.py 安装。")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    models = local_models(manifest_path, manifest)
    os.environ["HF_HUB_OFFLINE"] = "1"
    try:
        import numpy as np
        from PIL import Image
        import torch
        from anime_face_detector import LandmarkDetector
    except ImportError as exc:
        raise ValueError("缺少二次元检测依赖。使用 Python 3.12：python -m pip install anime-face-detector==0.1.0 Pillow；详情见 Assets/形态建拟合器/Documentation/自动标点安装.zh-CN.md。") from exc
    if version("anime-face-detector") != manifest["packageVersion"]:
        raise ValueError("anime-face-detector 版本与配置不符，请安装 " + manifest["packageVersion"])
    torch.set_num_threads(int(manifest["threads"]))
    torch.manual_seed(0)
    mapping = json.loads(Path(args.mapping).read_text(encoding="utf-8-sig"))
    # Input is exactly Unity's displayed PNG; no second EXIF transform.
    with Image.open(args.image) as source:
        rgb = source.convert("RGB")
    width, height = rgb.size
    if min(width, height) < 2:
        raise ValueError("图片尺寸过小。")
    detector = LandmarkDetector(landmark_checkpoint_path=models["landmark"], face_detector_name="yolov3",
        face_detector_checkpoint_path=models["face"], device="cpu", flip_test=bool(manifest["flipTest"]),
        box_scale_factor=float(manifest["boxScaleFactor"]))
    predictions = detector(np.asarray(rgb)[:, :, ::-1].copy())
    faces = [p for p in predictions if len(p["bbox"]) == 5 and np.isfinite(p["bbox"]).all()
             and p["bbox"][4] >= manifest["faceThreshold"] and p["bbox"][2] > p["bbox"][0]
             and p["bbox"][3] > p["bbox"][1]]
    meta = dict(schema=1, engine="hysts-anime-face-detector", engineLabel="二次元 28 点（YOLOv3 + HRNetV2）",
        imageWidth=width, imageHeight=height, imageSha256=args.source_hash or sha(args.image),
        coordinateSystem="normalized_xy_top_left", templateId=mapping["templateId"],
        pythonVersion=sys.version, packageVersion=version("anime-face-detector"), torchVersion=torch.__version__,
        numpyVersion=np.__version__, modelConfig=manifest, modelSha256=sha(manifest_path),
        mappingSha256=sha(args.mapping), detectorInputSha256=sha(args.image), seed=0,
        faceCount=len(faces), rawFaceCount=len(predictions), seconds=time.perf_counter()-started,
        unsupportedPoints=mapping["unsupported"],
        rawPredictions=[{k:v.tolist() for k,v in p.items()} for p in predictions],
        annotationNote="二次元 28 点自动候选。未支持的点不插值；热图响应不是正确率，遮挡、脸廓、眉及唇部需检查。")
    if not faces:
        return dict(meta, status="no_face", message="未检测到达到阈值的二次元人脸。可裁剪为近正面单人脸后重试，或手工标注。", points=[])
    if len(faces) != 1:
        return dict(meta, status="multiple_faces", message="检测到多张二次元脸，请裁剪为单人图片后重试。", points=[])
    points, excluded = map_points(faces[0]["keypoints"], mapping, width, height, manifest["keypointThreshold"])
    meta["excludedPoints"] = excluded
    by_id = {p["id"]:p for p in points}
    if "left_eye_outer" in by_id and "right_eye_outer" in by_id and by_id["left_eye_outer"]["x"] >= by_id["right_eye_outer"]["x"]:
        return dict(meta, status="unsupported_pose", message="眼部左右方向异常，请转正图片或手工标注。", points=[])
    if len(points) < 4 or not any("eye" in n for n in by_id) or not any(n == "chin" or n.startswith("mouth") for n in by_id):
        return dict(meta, status="insufficient_landmarks", message="检测点不足以约束拟合，请补充手工标点。", points=[])
    return dict(meta, status="ok", message=f"检测到 {len(points)} / 28 个二次元候选点；其余模板点不自动补造。请检查后使用。", points=points)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("image", "model", "mapping", "output"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--source-hash", default="")
    args = parser.parse_args()
    try:
        result = detect(args)
    except Exception as exc:
        result = dict(schema=1, status="error", message=str(exc), points=[])
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_suffix(".tmp")
    temp.write_text(json.dumps(json_safe(result), ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")
    temp.replace(output)
    print(json.dumps({k:result.get(k) for k in ("status", "message", "faceCount", "seconds")}, ensure_ascii=False))
    return 0 if result["status"] == "ok" else 2


if __name__ == "__main__":
    raise SystemExit(main())
