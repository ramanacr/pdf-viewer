#!/usr/bin/env bash
# Runs the Maestro scenarios against a connected device or emulator and prints one line per flow:
#   PASS <flow>   or   FAIL <flow>: <reason>   (with a screenshot path and the app's crash lines)
# Usage: e2e/run.sh [--install <apk>] [flow-name-filter]
# Each flow names, in its header comments, the document it starts on (# doc: sample|scan|none) and,
# for tools that save a file, what the file must be (# expect-file: <glob in /sdcard/Download>,
# # expect-grep: <regex the file must contain>). Files are checked on the host, not on screen.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HERE/../../.." && pwd)"
ADB="${ADB:-adb}"; MAESTRO="${MAESTRO:-maestro}"
APP=io.github.ramanacr.pdfviewer
ACTIVITY="$APP/crc64fd395df73aaabfab.MainActivity"
OUT="${E2E_OUT:-$HERE/out}"; mkdir -p "$OUT"
if [ "${1:-}" = "--install" ]; then "$ADB" install -r "$2" >/dev/null || { echo "install failed"; exit 1; }; shift 2; fi
FILTER="${1:-}"

"$ADB" get-state >/dev/null 2>&1 || { echo "no device"; exit 1; }
"$ADB" shell settings put global hide_error_dialogs 1 >/dev/null 2>&1

# Fixtures: the repository's sample, and an image-only scan made here (for OCR). They live outside
# /sdcard/Download, where the tools save and expect-file globs delete, so no glob can remove them.
FIX=/sdcard/Documents/pdfviewer-e2e
"$ADB" shell mkdir -p "$FIX"
python3 "$HERE/make_scan.py" "$OUT/scan.pdf" || exit 1
push() { "$ADB" push "$1" "$FIX/$2" >/dev/null && "$ADB" shell am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d "file://$FIX/$2" >/dev/null; }
push "$ROOT/samples/SampleDocument.pdf" SampleDocument.pdf
push "$OUT/scan.pdf" scan.pdf
sleep 3
media_id() { "$ADB" shell content query --uri content://media/external/file --projection _id --where "\"_data LIKE '%/pdfviewer-e2e/$1'\"" | sed -n 's/.*_id=\([0-9]*\).*/\1/p' | head -1 | tr -d '\r'; }
ID_sample=$(media_id SampleDocument.pdf); ID_scan=$(media_id scan.pdf)
[ -n "$ID_sample" ] && [ -n "$ID_scan" ] || { echo "fixtures not in the media store"; exit 1; }

pass=0; fail=0
for flow in "$HERE"/flows/*.yaml; do
  name=$(basename "$flow" .yaml)
  [ -n "$FILTER" ] && [[ "$name" != *"$FILTER"* ]] && continue
  doc=$(sed -n 's/^# doc: *//p' "$flow"); glob=$(sed -n 's/^# expect-file: *//p' "$flow"); grepre=$(sed -n 's/^# expect-grep: *//p' "$flow")
  # A clean start on the flow's document; files a flow is to make must not be there before it.
  "$ADB" shell am force-stop $APP; "$ADB" logcat -c
  [ -n "$glob" ] && "$ADB" shell "rm -f /sdcard/Download/$glob" 2>/dev/null
  case "$doc" in
    sample) "$ADB" shell am start -a android.intent.action.VIEW -d "content://media/external/file/$ID_sample" -t application/pdf --grant-read-uri-permission -n "$ACTIVITY" >/dev/null ;;
    scan)   "$ADB" shell am start -a android.intent.action.VIEW -d "content://media/external/file/$ID_scan" -t application/pdf --grant-read-uri-permission -n "$ACTIVITY" >/dev/null ;;
    *)      "$ADB" shell am start -n "$ACTIVITY" >/dev/null ;;
  esac
  reason=""
  if ! "$MAESTRO" test --no-ansi "$flow" > "$OUT/$name.log" 2>&1; then
    reason=$(grep -m1 -iE 'failed|not found|error|assertion' "$OUT/$name.log" | cut -c1-160)
    [ -z "$reason" ] && reason="maestro failed (see $OUT/$name.log)"
  fi
  if [ -z "$reason" ] && [ -n "$glob" ]; then
    file=$("$ADB" shell "ls /sdcard/Download/$glob 2>/dev/null" | head -1 | tr -d '\r')
    if [ -z "$file" ]; then reason="no file /sdcard/Download/$glob"
    else
      "$ADB" pull "$file" "$OUT/$name.out" >/dev/null 2>&1
      [ -s "$OUT/$name.out" ] || reason="empty file $file"
      [ -z "$reason" ] && [ -n "$grepre" ] && ! grep -aqE "$grepre" "$OUT/$name.out" && reason="file lacks /$grepre/"
    fi
  fi
  crash=$("$ADB" logcat -d | grep -E 'FATAL EXCEPTION|E AndroidRuntime|F monodroid' | head -3)
  [ -z "$reason" ] && [ -n "$crash" ] && reason="crash: $(echo "$crash" | head -1 | cut -c1-140)"
  if [ -z "$reason" ]; then echo "PASS $name"; pass=$((pass+1))
  else
    "$ADB" exec-out screencap -p > "$OUT/$name.png" 2>/dev/null
    echo "FAIL $name: $reason  [screen: $OUT/$name.png, log: $OUT/$name.log]"; fail=$((fail+1))
  fi
done
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
