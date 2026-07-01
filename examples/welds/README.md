echo "Rocket CoreIVASpaceA_Prefab_MediumCapsuleA" > /sim/debug/vessels/Hunter/weld

# 2. weld Hunter (source = the path) to that part with your known pose
#    format: <target> <part_iid> <x y z> <pitch yaw roll> <lock>

```
echo "Rocket $(rg IVA /sim/vessels/by-id/Rocket/parts --glob '/sim/vessels/by-id/Rocket/parts/*/id' -l | sed 's|/id$|/instance_id|' | xargs cat) -0.5 -0.45 -0.15 0 0 0 1" > /sim/debug/vessels/Hunter/weld



echo 1 > /sim/debug/welds/clear
```