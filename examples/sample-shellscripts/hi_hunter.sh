#!/bin/bash

part_instance_id=$(cat /sim/vessels/by-id/$VESSEL/parts/json | jq -r '.. | objects | select(.id? and (.id | contains("flexo_button_"))) | .instance_id')

echo "${VESSEL} ${part_instance_id} 0 0.6 0 -25 90 -90 1" > /sim/debug/vessels/Hunter/weld


