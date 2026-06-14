# install astroterm

```bash
apk add astroterm
astroterm -u -C -c -s 100 -f 30 -i auckland
```

# install mqttui

use mqttui to explore the MQTT data feeds available at `mqtt://sim`

```bash
apk add mqttui
mqttui -b mqtt://sim
```

# explore the ksa device filesystem

```bash
# set active vehicle throttle ot 50%
echo 0.5 > /sim/vessels/active/ctl/throttle

# check the active vehicle engine(1) throttle
cat /sim/vessels/active/engines/1/throttle

```



TODO: test cargo compile of dashboard-rs now that hypervisor accel is working

