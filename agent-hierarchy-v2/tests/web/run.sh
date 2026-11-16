#!/bin/sh
# The web client's tests: node's built-in runner, no npm. AH_WEB_GOLDEN=update rewrites the goldens.
here=$(cd "$(dirname "$0")" && pwd)
exec node --disable-warning=MODULE_TYPELESS_PACKAGE_JSON --import "$here/register.mjs" --test "$here"/*.test.mjs
