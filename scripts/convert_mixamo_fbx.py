"""把 Downloads/dongzuo 里的 Mixamo FBX 转成只含动画的 GLB。"""
import bpy
from pathlib import Path

src_dir = Path("/Users/daishan/Downloads/dongzuo")
dst_dir = Path("/Users/daishan/starve-godot/GodotClient/assets/models/pigman/mixamo")
dst_dir.mkdir(parents=True, exist_ok=True)


def convert(fbx: Path) -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(
        filepath=str(fbx),
        automatic_bone_orientation=False,
        ignore_leaf_bones=False,
        use_anim=True,
    )
    for obj in list(bpy.data.objects):
        if obj.type == "MESH":
            bpy.data.objects.remove(obj, do_unlink=True)

    clip = fbx.stem
    actions = list(bpy.data.actions)
    if not actions:
        raise SystemExit(f"{fbx.name} 没有动画")
    keep = actions[0]
    keep.name = clip
    for action in actions[1:]:
        bpy.data.actions.remove(action)

    arm = next((o for o in bpy.data.objects if o.type == "ARMATURE"), None)
    if arm is not None:
        if arm.animation_data is None:
            arm.animation_data_create()
        arm.animation_data.action = keep

    dst = dst_dir / f"{clip}.glb"
    bpy.ops.export_scene.gltf(
        filepath=str(dst),
        export_format="GLB",
        export_animations=True,
        export_skins=True,
        export_cameras=False,
        export_lights=False,
        export_anim_single_armature=True,
    )
    print(f"wrote {dst} ({dst.stat().st_size} bytes) from {fbx.name} action={keep.name}")


for fbx in sorted(src_dir.glob("*.fbx")):
    convert(fbx)
