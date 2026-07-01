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

```
 ~/thug.ts --id Hunter --meters="-1" --time 1.2 --hz 120 --easing ease-out
 ```
 echo '0.25 0 -0.33' > /sim/debug/thug_life/0/position