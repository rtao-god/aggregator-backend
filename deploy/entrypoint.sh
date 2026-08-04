#!/bin/sh
set -eu

if [ -z "${APP_DLL:-}" ]; then
  echo "APP_DLL is required." >&2
  exit 64
fi

case "$APP_DLL" in
  *[!A-Za-z0-9._-]*|.*|*/*|*\\*)
    echo "APP_DLL must be a relative assembly filename." >&2
    exit 64
    ;;
esac

if [ ! -f "/app/$APP_DLL" ]; then
  echo "Application assembly '/app/$APP_DLL' does not exist." >&2
  exit 66
fi

exec dotnet "/app/$APP_DLL"
