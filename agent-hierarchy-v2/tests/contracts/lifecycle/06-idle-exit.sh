#!/bin/bash
# K5 scenario 6: idle exit. After AHD_IDLE_EXIT_MS with no request, no timer and no live registration the daemon exits.
# Negative: the same daemon with a live registration (a pid that is alive) has not exited at the same time.
. "$(dirname "$0")/_lib.sh"
lc_runtimes
lc_init
export AHD_IDLE_EXIT_MS=1500

# idle: the daemon is gone after 4 s
lc_ctl start >/dev/null 2>&1
D=$(lc_info_pid)
lc_check "a daemon is up" 'lc_alive "$D"'
sleep 4
lc_check "with nothing registered it has exited after 4 s (idle limit 1.5 s)" '! lc_alive "$D"'

# with a live registration it stays (the registration route is the mesh stream's: a native daemon does not serve it yet)
if [ "$IMPL_DAEMON" = native ]; then
  lc_skip "with a live registration the daemon stays up" "POST /v1/register is not served natively until the mesh stream lands"
else
  # with a live registration it stays
  rm -f "$AH_HOME/run/ahd.json" "$AH_HOME/run/ahd.sock"
  lc_ctl start >/dev/null 2>&1
  D2=$(lc_info_pid)
  BOX="$PROJ/boxes/idle-member"
  mkdir -p "$BOX"
  node -e '
  const http=require("http");
  const body=JSON.stringify({session_id:"s-idle",addresses:["idle-member@tm"],boxes:{"idle-member@tm":process.argv[2]},harness:"claude",pid:Number(process.argv[3])});
  const r=http.request({socketPath:process.argv[1],path:"/v1/register",method:"POST",headers:{"Content-Type":"application/json","Content-Length":Buffer.byteLength(body)}},(res)=>{res.resume();res.on("end",()=>console.log(res.statusCode))});
  r.on("error",()=>console.log("error"));r.end(body);' "$AH_HOME/run/ahd.sock" "$BOX" "$$" >"$T/reg.out"
  lc_check "a registration with a live pid is accepted" 'grep -q "^200$" "$T/reg.out"'
  sleep 4
  lc_check "with a live registration the daemon has not exited after the same 4 s" 'lc_alive "$D2"'
fi
lc_done
