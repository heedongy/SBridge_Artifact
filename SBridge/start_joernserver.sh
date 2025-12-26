#!/bin/bash

JOERN_EXEC="/home/user/tools/joern/joern"
JOERN_OPTS="--server --server-host localhost --server-port 8081"

if pgrep -f "$JOERN_EXEC $JOERN_OPTS" > /dev/null; then
    echo "Joern server is already running. Stopping it..."
    pkill -f "$JOERN_EXEC $JOERN_OPTS"
    sleep 2
fi

echo "Starting Joern server..."
nohup $JOERN_EXEC $JOERN_OPTS > /dev/null 2>&1 &
echo "Joern server started with PID $!"