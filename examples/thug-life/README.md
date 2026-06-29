```bash
2676


echo "Hunter $(cat /sim/vessels/by-id/Hunter/parts/0/instance_id)" > /sim/debug/thug_life/add
sleep 1
THUG_NUM=0
echo '90 180 90' > /sim/debug/thug_life/$THUG_NUM/rotation
echo '0.9 0.22' > /sim/debug/thug_life/$THUG_NUM/size
echo '0.23 0 -0.33' > /sim/debug/thug_life/$THUG_NUM/position

echo 1 > /sim/debug/thug_life/clear
```