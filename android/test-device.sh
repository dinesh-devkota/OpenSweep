#!/usr/bin/env bash
set -euo pipefail

# Run only on a disposable emulator; tests create and remove their own unique fixtures.
serial="${ANDROID_SERIAL:-emulator-${EMULATOR_PORT:-5554}}"
if [[ "$serial" != emulator-* ]] || [[ "$(adb -s "$serial" shell getprop ro.kernel.qemu | tr -d '\r')" != 1 ]]; then
  echo 'Device regression checks require an isolated Android emulator.' >&2
  exit 1
fi
mkdir -p build/device-reports
adb -s "$serial" install -r app/build/outputs/apk/debug/app-debug.apk
adb -s "$serial" install -r app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk
sdk="$(adb -s "$serial" shell getprop ro.build.version.sdk | tr -d '\r')"
if (( sdk < 30 )); then
  adb -s "$serial" shell pm grant io.opensweep.android.debug android.permission.READ_EXTERNAL_STORAGE
  adb -s "$serial" shell pm grant io.opensweep.android.debug android.permission.WRITE_EXTERNAL_STORAGE
  stages=(legacy)
else
  adb -s "$serial" shell appops set --uid io.opensweep.android.debug MANAGE_EXTERNAL_STORAGE allow
  stages=(prepare recover)
fi
for stage in "${stages[@]}"; do
  if [[ "$stage" == recover ]]; then
    adb -s "$serial" shell am force-stop io.opensweep.android.debug
  fi
  adb -s "$serial" shell am instrument -w -r -e stage "$stage" \
    io.opensweep.android.debug.test/io.opensweep.android.CleanupRegressionRunner \
    | tr -d '\r' | tee "build/device-reports/$stage.txt"
  grep -Fxq 'INSTRUMENTATION_CODE: -1' "build/device-reports/$stage.txt"
  grep -Eq "OpenSweep device regression: [0-9]+ checks passed \($stage\)" "build/device-reports/$stage.txt"
  if grep -q 'INSTRUMENTATION_RESULT: failure=' "build/device-reports/$stage.txt"; then exit 1; fi
done
