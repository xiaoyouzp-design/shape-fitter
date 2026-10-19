"""Local MediaPipe image landmarks; candidates only, never model correspondences."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import sys


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def map_points(face, mapping, width, height):
    points = []
    for name, indices in mapping["points"].items():
        x = sum(face[i].x for i in indices) / len(indices) * width / (width - 1)
        y = sum(face[i].y for i in indices) / len(indices) * height / (height - 1)
        if math.isfinite(x) and math.isfinite(y) and 0 <= x <= 1 and 0 <= y <= 1:
            points.append(dict(id=name, state="marked", x=x, y=y))
    return points


def detect(args):
    if not Path(args.model).is_file():
        raise ValueError("缺少 Face Landmarker 模型文件，请在自动标点设置指定本地 face_landmarker.task。")
    try:
        import mediapipe as mp
        import numpy as np
        from PIL import Image
    except ImportError as exc:
        raise ValueError("缺少检测依赖。使用 Python 3.12 环境安装：python -m pip install mediapipe==0.10.32 Pillow") from exc
    mapping = json.loads(Path(args.mapping).read_text(encoding="utf-8-sig"))
    # Unity receives an upright PNG of its displayed Texture2D. No second EXIF transform.
    with Image.open(args.image) as source:
        rgb = source.convert("RGB")
    width, height = rgb.size
    if min(width, height) < 2:
        raise ValueError("图片尺寸过小。")
    options = mp.tasks.vision.FaceLandmarkerOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(Path(args.model).resolve())),
        running_mode=mp.tasks.vision.RunningMode.IMAGE,
        num_faces=2, min_face_detection_confidence=0.5,
        min_face_presence_confidence=0.5)
    with mp.tasks.vision.FaceLandmarker.create_from_options(options) as detector:
        result = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB, data=np.array(rgb)))
    meta = dict(schema=1, engine="mediapipe", engineLabel="真人 MediaPipe", imageWidth=width, imageHeight=height,
                imageSha256=args.source_hash or sha(args.image),
                coordinateSystem="normalized_xy_top_left", templateId=mapping["templateId"],
                mediapipeVersion=mp.__version__, pythonVersion=sys.version,
                modelSha256=sha(args.model), mappingSha256=sha(args.mapping),
                detectorInputSha256=sha(args.image), faceCount=len(result.face_landmarks),
                annotationNote="MediaPipe 自动候选；未经人工确认。左右按画面；轮廓/眉/唇对应近似，遮挡与二次元可能失效。")
    if not result.face_landmarks:
        return dict(meta, status="no_face", message="未检测到人脸。二次元或遮挡图片可能不支持，请改用手工标点。", points=[])
    if len(result.face_landmarks) != 1:
        return dict(meta, status="multiple_faces", message="检测到多张脸，请裁剪为单人图片后重试。", points=[])
    points = map_points(result.face_landmarks[0], mapping, width, height)
    by_id = {p["id"]: p for p in points}
    if "left_eye_outer" not in by_id or "right_eye_outer" not in by_id or by_id["left_eye_outer"]["x"] >= by_id["right_eye_outer"]["x"]:
        return dict(meta, status="unsupported_pose", message="左右眼方向异常，请转正图片或手工标注。", points=[])
    return dict(meta, status="ok", message=f"检测到 {len(points)} 个候选点，请检查后使用。", points=points)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--image", required=True)
    p.add_argument("--model", required=True)
    p.add_argument("--mapping", required=True)
    p.add_argument("--output", required=True)
    p.add_argument("--source-hash", default="")
    args = p.parse_args()
    try:
        result = detect(args)
    except Exception as exc:
        result = dict(schema=1, status="error", message=str(exc), points=[])
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    temp = output.with_suffix(".tmp")
    temp.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    temp.replace(output)
    print(json.dumps({k: result.get(k) for k in ("status", "message", "faceCount")}, ensure_ascii=False))
    return 0 if result["status"] == "ok" else 2


if __name__ == "__main__":
    raise SystemExit(main())
