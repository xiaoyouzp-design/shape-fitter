"""Fetch pinned safetensors weights from the detector author's public repositories."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
from concurrent.futures import ThreadPoolExecutor

ROOT = Path(__file__).resolve().parent / "photo_auto_models" / "anime"
MODELS = {
    "face": ("hysts/anime-face-detector-yolov3", "afdd4226a79ae8bb81f334dbcffd34f8cc000c38"),
    "landmark": ("hysts/anime-face-detector-hrnetv2", "9b3435248b26aeb82e2a8578fe9d86d5d57158af"),
}


def download(item):
    kind, (repo, revision) = item
    listing = json.load(urllib.request.urlopen(f"https://huggingface.co/api/models/{repo}/tree/{revision}", timeout=30))
    metadata = next(f for f in listing if f["path"] == "model.safetensors")
    expected = metadata["lfs"]["oid"]
    url = f"https://huggingface.co/{repo}/resolve/{revision}/model.safetensors"
    path = ROOT / (kind + ".safetensors")
    if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        temp = path.with_suffix(".download")
        with urllib.request.urlopen(url, timeout=60) as response, temp.open("wb") as file:
            while block := response.read(1024 * 1024):
                file.write(block)
        if hashlib.sha256(temp.read_bytes()).hexdigest() != expected:
            raise ValueError(f"Downloaded checkpoint hash mismatch: {kind}")
        temp.replace(path)
    return kind, dict(file=path.name, sha256=expected, repo=repo, revision=revision, url=url, license="MIT")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-root", type=Path, default=ROOT)
    ROOT = parser.parse_args().output_root.resolve()
    ROOT.mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=2) as pool:
        models = dict(pool.map(download, MODELS.items()))
    manifest = dict(schema=1, engine="hysts-anime-face-detector", packageVersion="0.1.0", device="cpu",
                    faceThreshold=0.5, keypointThreshold=0.5, flipTest=True, boxScaleFactor=1.1,
                    threads=4, models=models)
    (ROOT / "engine.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(manifest, ensure_ascii=False, indent=2))
