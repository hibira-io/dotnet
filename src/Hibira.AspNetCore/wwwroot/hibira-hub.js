/*!
 * hibira-hub.js 0.1.0 - browser client for Hibira.AspNetCore hubs.
 * The API mirrors @microsoft/signalr's HubConnection. Needs pusher-js 8
 * (window.Pusher, or pass it as the `Pusher` option).
 * MIT License, https://github.com/hibira-io/dotnet
 */
(function (root, factory) {
  if (typeof module === "object" && module.exports) module.exports = factory();
  else root.hibira = factory();
})(typeof self !== "undefined" ? self : this, function () {
  "use strict";

  var HubConnectionState = Object.freeze({
    Disconnected: "Disconnected",
    Connecting: "Connecting",
    Connected: "Connected",
    Disconnecting: "Disconnecting",
    Reconnecting: "Reconnecting",
  });

  function HubConnectionBuilder() {
    this._url = null;
    this._options = {};
    this._reconnect = false;
  }

  /**
   * The hub URL, as mapped with app.MapHibiraHub<T>("/hubs/chat").
   * options: { headers, accessTokenFactory, withCredentials, Pusher, pusherOptions }
   */
  HubConnectionBuilder.prototype.withUrl = function (url, options) {
    this._url = url;
    this._options = options || {};
    return this;
  };

  /** Reconnect after the connection drops (with a new connection ID, as in SignalR). */
  HubConnectionBuilder.prototype.withAutomaticReconnect = function () {
    this._reconnect = true;
    return this;
  };

  /** Accepted for SignalR compatibility; logging goes to the console. */
  HubConnectionBuilder.prototype.configureLogging = function () {
    return this;
  };

  HubConnectionBuilder.prototype.build = function () {
    if (!this._url) throw new Error("hibira: call withUrl(url) before build()");
    return new HubConnection(this._url, this._options, this._reconnect);
  };

  function HubConnection(url, options, reconnect) {
    this.baseUrl = String(url).replace(/\/+$/, "");
    this._options = options;
    this._reconnect = reconnect;
    this._handlers = {};
    this._closed = [];
    this._reconnecting = [];
    this._reconnected = [];
    this._state = HubConnectionState.Disconnected;
    this._pusher = null;
    this._info = null;
    this._socketId = null;
    this._token = null;
    this._groups = {};
    this._connChannel = null;
    this._handshake = null;
    this._onPageHide = this._pageHide.bind(this);
  }

  Object.defineProperty(HubConnection.prototype, "state", {
    get: function () { return this._state; },
  });

  Object.defineProperty(HubConnection.prototype, "connectionId", {
    get: function () { return this._state === HubConnectionState.Connected ? this._socketId : null; },
  });

  /** Calls handler(...args) when the server sends `method`. */
  HubConnection.prototype.on = function (method, handler) {
    var key = String(method).toLowerCase();
    (this._handlers[key] = this._handlers[key] || []).push(handler);
  };

  /** Removes one handler, or every handler of `method`. */
  HubConnection.prototype.off = function (method, handler) {
    var key = String(method).toLowerCase();
    if (!handler) delete this._handlers[key];
    else if (this._handlers[key]) this._handlers[key] = this._handlers[key].filter(function (h) { return h !== handler; });
  };

  HubConnection.prototype.onclose = function (cb) { this._closed.push(cb); };
  HubConnection.prototype.onreconnecting = function (cb) { this._reconnecting.push(cb); };
  HubConnection.prototype.onreconnected = function (cb) { this._reconnected.push(cb); };

  HubConnection.prototype.start = async function () {
    if (this._state !== HubConnectionState.Disconnected)
      throw new Error("hibira: the connection is not in the Disconnected state");
    this._state = HubConnectionState.Connecting;
    try {
      this._info = await this._request("GET", "negotiate");
      var Pusher = this._options.Pusher || (typeof window !== "undefined" ? window.Pusher : undefined);
      if (!Pusher) throw new Error("hibira: pusher-js is not loaded (add it before hibira-hub.js, or pass the Pusher option)");
      this._pusher = new Pusher(this._info.key, this._pusherOptions(this._info));
      var socketId = await this._waitConnected();
      this._pusher.connection.bind("state_change", this._stateChange.bind(this));
      var all = this._pusher.subscribe(this._info.channel);
      all.bind_global(this._dispatch.bind(this));
      if (this._info.userId) {
        this._pusher.signin();
        this._pusher.user.bind_global(this._dispatch.bind(this));
      }
      await this._setUp(socketId);
      this._state = HubConnectionState.Connected;
      if (typeof window !== "undefined") window.addEventListener("pagehide", this._onPageHide);
    } catch (e) {
      this._teardown();
      this._state = HubConnectionState.Disconnected;
      throw e;
    }
  };

  HubConnection.prototype.stop = async function () {
    if (this._state === HubConnectionState.Disconnected) return;
    this._state = HubConnectionState.Disconnecting;
    if (this._token) {
      try {
        await this._request("POST", "disconnect", { connectionId: this._socketId, token: this._token });
      } catch (e) { /* the webhook or idle timeout cleans up */ }
    }
    this._close(undefined);
  };

  /** Calls a hub method and resolves with its return value. */
  HubConnection.prototype.invoke = async function (method) {
    var args = Array.prototype.slice.call(arguments, 1);
    if (this._state !== HubConnectionState.Connected)
      throw new Error("hibira: cannot invoke '" + method + "' when the connection is not in the Connected state");
    var res = await this._request("POST", "invoke", {
      connectionId: this._socketId,
      token: this._token,
      target: method,
      arguments: args,
    });
    if (res.error) throw new Error(res.error);
    return res.result;
  };

  /** Calls a hub method without waiting for its return value. */
  HubConnection.prototype.send = async function (method) {
    await this.invoke.apply(this, arguments);
  };

  HubConnection.prototype._pusherOptions = function (info) {
    var self = this;
    var o = Object.assign({
      cluster: "hibira",
      wsHost: info.host,
      wsPort: info.port,
      wssPort: info.port,
      forceTLS: info.useTls,
      enabledTransports: ["ws", "wss"],
      disableStats: true,
    }, this._options.pusherOptions || {});
    o.channelAuthorization = {
      customHandler: function (params, callback) {
        self._form("auth", { socket_id: params.socketId, channel_name: params.channelName })
          .then(function (r) { callback(null, r); }, function (e) { callback(e, null); });
      },
    };
    o.userAuthentication = {
      customHandler: function (params, callback) {
        self._form("user-auth", { socket_id: params.socketId })
          .then(function (r) { callback(null, r); }, function (e) { callback(e, null); });
      },
    };
    return o;
  };

  HubConnection.prototype._waitConnected = function () {
    var conn = this._pusher.connection;
    return new Promise(function (resolve, reject) {
      function done(err) {
        conn.unbind("connected", ok);
        conn.unbind("failed", fail);
        conn.unbind("unavailable", fail);
        if (err) reject(err); else resolve(conn.socket_id);
      }
      function ok() { done(null); }
      function fail() { done(new Error("hibira: could not connect to " + conn.options.wsHost)); }
      conn.bind("connected", ok);
      conn.bind("failed", fail);
      conn.bind("unavailable", fail);
    });
  };

  // Subscribe to this socket's own channel, get the connection token over
  // it, and tell the hub we are here.
  HubConnection.prototype._setUp = async function (socketId) {
    var self = this;
    this._socketId = socketId;
    this._token = null;
    var name = this._info.connectionChannelPrefix + socketId;
    var channel = this._pusher.subscribe(name);
    this._connChannel = name;
    channel.bind_global(this._dispatch.bind(this));
    await new Promise(function (resolve, reject) {
      channel.bind("pusher:subscription_succeeded", resolve);
      channel.bind("pusher:subscription_error", function (e) {
        reject(new Error("hibira: could not subscribe to the connection channel (" + JSON.stringify(e) + ")"));
      });
    });
    var token = new Promise(function (resolve, reject) {
      var timer = setTimeout(function () { reject(new Error("hibira: no handshake from the hub")); }, 10000);
      self._handshake = function (t) { clearTimeout(timer); resolve(t); };
    });
    await this._request("POST", "handshake", { connectionId: socketId });
    this._token = await token;
    var res = await this._request("POST", "connect", { connectionId: socketId, token: this._token });
    if (res.error) throw new Error(res.error);
  };

  HubConnection.prototype._dispatch = function (event, data) {
    if (event.indexOf("pusher:") === 0 || event.indexOf("pusher_internal:") === 0) return;
    if (event === "hibira:handshake") {
      if (this._handshake) { this._handshake(data.token); this._handshake = null; }
      return;
    }
    if (event === "hibira:join") {
      if (!this._groups[data.channel]) {
        this._groups[data.channel] = true;
        this._pusher.subscribe(data.channel).bind_global(this._dispatch.bind(this));
      }
      return;
    }
    if (event === "hibira:leave") {
      delete this._groups[data.channel];
      this._pusher.unsubscribe(data.channel);
      return;
    }
    if (!data || data.hub !== this._info.hub) return;
    var handlers = this._handlers[event.toLowerCase()];
    if (!handlers) {
      console.warn("hibira: no client method with the name '" + event + "' found.");
      return;
    }
    var args = data.arguments || [];
    handlers.slice().forEach(function (h) {
      try { h.apply(null, args); } catch (e) { console.error("hibira: a handler for '" + event + "' threw", e); }
    });
  };

  HubConnection.prototype._stateChange = function (s) {
    if (s.previous === "connected" && s.current !== "connected" && this._state === HubConnectionState.Connected) {
      this._dropConnectionChannels();
      if (!this._reconnect) {
        this._close(new Error("hibira: the connection was lost"));
        return;
      }
      this._state = HubConnectionState.Reconnecting;
      var err = new Error("hibira: the connection was lost; reconnecting");
      this._reconnecting.forEach(function (cb) { cb(err); });
    } else if (s.current === "connected" && this._state === HubConnectionState.Reconnecting) {
      var self = this;
      this._setUp(this._pusher.connection.socket_id).then(function () {
        self._state = HubConnectionState.Connected;
        self._reconnected.forEach(function (cb) { cb(self._socketId); });
      }, function (e) {
        self._close(e);
      });
    }
  };

  // A new socket is a new connection: its old connection and group
  // channels would be refused, so drop them before pusher-js resubscribes.
  HubConnection.prototype._dropConnectionChannels = function () {
    var p = this._pusher;
    if (this._connChannel) p.unsubscribe(this._connChannel);
    Object.keys(this._groups).forEach(function (c) { p.unsubscribe(c); });
    this._groups = {};
    this._connChannel = null;
    this._token = null;
  };

  HubConnection.prototype._close = function (error) {
    this._teardown();
    this._state = HubConnectionState.Disconnected;
    this._closed.forEach(function (cb) { cb(error); });
  };

  HubConnection.prototype._teardown = function () {
    if (typeof window !== "undefined") window.removeEventListener("pagehide", this._onPageHide);
    if (this._pusher) this._pusher.disconnect();
    this._pusher = null;
    this._groups = {};
    this._connChannel = null;
    this._token = null;
    this._socketId = null;
  };

  HubConnection.prototype._pageHide = function () {
    if (!this._token) return;
    try {
      this._fetch("disconnect", {
        method: "POST",
        keepalive: true,
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ connectionId: this._socketId, token: this._token }),
      });
    } catch (e) { /* ignore */ }
  };

  HubConnection.prototype._headers = async function (extra) {
    var h = Object.assign({}, this._options.headers || {}, extra || {});
    if (this._options.accessTokenFactory) {
      var t = await this._options.accessTokenFactory();
      if (t) h.Authorization = "Bearer " + t;
    }
    return h;
  };

  HubConnection.prototype._fetch = function (path, init) {
    init.credentials = this._options.withCredentials ? "include" : "same-origin";
    return fetch(this.baseUrl + "/" + path, init);
  };

  HubConnection.prototype._request = async function (method, path, body) {
    var init = { method: method, headers: await this._headers(body ? { "Content-Type": "application/json" } : null) };
    if (body) init.body = JSON.stringify(body);
    var res = await this._fetch(path, init);
    var json = null;
    try { json = await res.json(); } catch (e) { /* empty */ }
    if (!res.ok) throw new Error("hibira: " + path + " answered " + res.status + (json && json.error ? ": " + json.error : ""));
    return json || {};
  };

  HubConnection.prototype._form = async function (path, fields) {
    var res = await this._fetch(path, {
      method: "POST",
      headers: await this._headers({ "Content-Type": "application/x-www-form-urlencoded" }),
      body: new URLSearchParams(fields).toString(),
    });
    if (!res.ok) throw new Error("hibira: " + path + " answered " + res.status);
    return res.json();
  };

  return {
    HubConnectionBuilder: HubConnectionBuilder,
    HubConnection: HubConnection,
    HubConnectionState: HubConnectionState,
    version: "0.1.0",
  };
});
