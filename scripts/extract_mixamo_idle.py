"""从 three.js Xbot.glb 抽出 idle，去掉网格，留给猪人当 Mixamo clip。"""
import bpy
from pathlib import Path

src = Path("/tmp/mixamo-src/Xbot.glb")
dst = Path("/Users/daishan/starve-godot/GodotClient/assets/models/pigman/mixamo/idle.glb")
dst.parent.mkdir(parents=True, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(src))

for obj in list(bpy.data.objects):
    if obj.type == "MESH":
        bpy.data.objects.remove(obj, do_unlink=True)

keep = None
for action in list(bpy.data.actions):
    if action.name.lower() == "idle":
        keep = action
        continue
    bpy.data.actions.remove(action)

if keep is None:
    raise SystemExit("Xbot 里没有名为 idle 的 action")
keep.name = "idle"

bpy.ops.export_scene.gltf(
    filepath=str(dst),
    export_format="GLB",
    export_animations=True,
    export_skins=True,
    export_cameras=False,
    export_lights=False,
)
print("wrote", dst, "size", dst.stat().st_size)
