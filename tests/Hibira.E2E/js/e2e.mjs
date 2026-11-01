// The browser client, run under Node. Started by Program.cs with the hub URL.
import { createRequire } from 'node:module';
import Pusher from 'pusher-js';

const require = createRequire(import.meta.url);
const hibira = require('../../../src/Hibira.AspNetCore/wwwroot/hibira-hub.js');
const url = process.argv[2];

const got = [];
const wait = async (msg, ms = 8000) => {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    if (got.includes(msg)) return true;
    await new Promise((r) => setTimeout(r, 25));
  }
  return false;
};
let failed = 0;
const check = (ok, what) => {
  console.log((ok ? 'ok    ' : 'FAIL  ') + what);
  if (!ok) failed++;
};

const conn = new hibira.HubConnectionBuilder()
  .withUrl(url, { Pusher, headers: { 'X-User': 'carol' } })
  .withAutomaticReconnect()
  .build();
for (const m of ['ReceiveMessage', 'Room', 'Direct', 'Echo']) {
  conn.on(m, (from, text) => got.push(`${m}:${from}:${text}`));
}

try {
  await conn.start();
  check(conn.state === hibira.HubConnectionState.Connected, `connected as ${conn.connectionId}`);
  check((await conn.invoke('Add', 40, 2)) === 42, 'invoke returns the result');
  await conn.invoke('Echo', 'js');
  check(await wait('Echo:carol:js'), 'Clients.Caller reaches the browser client');
  await conn.invoke('Join', 'js');
  try {
    await conn.invoke('Fail');
    check(false, 'hub errors reject invoke');
  } catch (e) {
    check(String(e.message).includes('nope'), 'hub errors reject invoke');
  }
  console.log('READY');
  check(await wait('ReceiveMessage:ann:from-dotnet'), 'Clients.All from a .NET client');
  check(await wait('Room:ann:to-js-room'), 'Clients.Group after Join');
  check(await wait('Direct:ann:to-carol'), 'Clients.User after sign-in');
  await conn.send('SendMessage', 'from-js');
  await new Promise((r) => setTimeout(r, 500));
  await conn.stop();
  check(conn.state === hibira.HubConnectionState.Disconnected, 'stop disconnects');
} catch (e) {
  check(false, 'unexpected error: ' + (e && e.stack ? e.stack : e));
}
process.exit(failed ? 1 : 0);
