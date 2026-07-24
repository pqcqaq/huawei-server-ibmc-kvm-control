#!/bin/sh
set -eu

result_path=${1:?result path is required}
rm -f -- "$result_path"
IFS= read -r line
printf '%s\n' "$line" > "$result_path"
sleep 3
