/*
 * offline-guard.js —— 离线守门人（M7.5 S1）
 *
 * 目的：把「断网可用」从一句承诺，变成一个**可断言的事实**。
 *
 * 做法：装在所有脚本的最前面（包括 GeoGebra 自己的 GWT 装载器之前），
 * 把一切「离开本虚拟域名」的网络出口拦下来：
 *     fetch / XMLHttpRequest / WebSocket / EventSource / sendBeacon
 *     script.src / link.href / img.src / media.src / source.src
 *
 * 拦截后：记一条违规（console + 回宿主一条 error 消息）+ 让这次请求失败。
 * 于是 —— 如果 GeoGebra 仍然完整跑起来、能画圆能拖点，就**证明了它一个外部字节都不需要**。
 *
 * ★ 为什么是「拦」而不是「只记录」：
 *   只记录的话，断网现场会出现「某个资源取不到 → 白屏」，
 *   而开发机上一切正常（因为开发机有网）—— 那是最难查的一类问题。
 *   拦住它，问题在开发当天就暴露，而不是在教室里暴露。
 *
 * ★ 一条不影响正确性的细节：相对地址与 data:/blob: 一律放行。
 *   本页本身就住在虚拟域名 geogebra.local 上，所以「相对地址」必然同源；
 *   GWT 的代码分片（deferredjs）与 GeoGebra 内联的图标都走这两条路，属正常。
 */
(function (global) {
  'use strict';

  var host = '';
  var violations = [];
  var notify = null;
  var installed = false;

  /** 解析成绝对 URL；解析不了返回 null（解析不了就不妄断，放行）。 */
  function resolve(url) {
    try {
      return new URL(String(url), global.location.href);
    } catch (e) {
      return null;
    }
  }

  /** 是否是「离开本域名」的请求。 */
  function isExternal(url) {
    if (url === null || url === undefined) return false;

    var u = resolve(url);
    if (!u) return false;

    // data: / blob: / about: / javascript: —— 都不是网络请求，放行。
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return false;

    return u.host !== host;
  }

  function report(url, kind, detail) {
    var entry = {
      url: String(url),
      kind: kind,
      at: new Date().toISOString()
    };

    if (detail !== undefined && detail !== null) entry.detail = String(detail);

    violations.push(entry);

    if (global.console && console.warn) {
      console.warn('[offline-guard] 拦截外部请求：' + kind + ' → ' + entry.url);
    }

    if (notify) {
      // 上报本身绝不能抛 —— 否则守门人会变成故障放大器。
      try {
        notify(entry);
      } catch (e) {
        /* 故意吞掉 */
      }
    }

    return entry;
  }

  /** 拦一个「构造即发起」的客户端（WebSocket / EventSource）。 */
  function guardConstructor(name, kind) {
    var Native = global[name];
    if (typeof Native !== 'function') return;

    function Guarded(url) {
      if (isExternal(url)) {
        report(url, kind);
        throw new Error('离线守门人已拦截外部连接：' + url);
      }

      // 用 Reflect.construct 保留原型链与 instanceof 语义。
      return Reflect.construct(Native, arguments, new.target || Native);
    }

    Guarded.prototype = Native.prototype;
    try {
      Object.setPrototypeOf(Guarded, Native);
    } catch (e) {
      /* 老引擎忽略 */
    }

    global[name] = Guarded;
  }

  /** 拦一个「取 URL 的属性 setter」（script.src / link.href / img.src …）。 */
  function guardUrlProperty(ctor, prop, kind) {
    if (typeof ctor !== 'function') return;

    var desc = Object.getOwnPropertyDescriptor(ctor.prototype, prop);
    if (!desc || typeof desc.set !== 'function') return;

    Object.defineProperty(ctor.prototype, prop, {
      configurable: true,
      enumerable: desc.enumerable,
      get: desc.get,
      set: function (value) {
        if (isExternal(value)) {
          report(value, kind);
          return;   // ★ 不设置 ⇒ 浏览器不会去加载它
        }

        desc.set.call(this, value);
      }
    });
  }

  function install(options) {
    if (installed) return false;
    installed = true;

    options = options || {};
    host = (global.location && global.location.host) ? global.location.host : '';
    notify = (typeof options.onViolation === 'function') ? options.onViolation : null;

    // ---------------------------------------------------------------- fetch
    var nativeFetch = (typeof global.fetch === 'function') ? global.fetch.bind(global) : null;
    if (nativeFetch) {
      global.fetch = function (input, init) {
        var url = (typeof input === 'string') ? input : (input && input.url) || '';

        if (isExternal(url)) {
          report(url, 'fetch');
          return Promise.reject(new TypeError('离线守门人已拦截外部请求：' + url));
        }

        return nativeFetch(input, init);
      };
    }

    // ------------------------------------------------------- XMLHttpRequest
    var XhrProto = global.XMLHttpRequest && global.XMLHttpRequest.prototype;
    if (XhrProto) {
      var nativeOpen = XhrProto.open;
      var nativeSend = XhrProto.send;

      XhrProto.open = function (method, url) {
        var blocked = isExternal(url);
        this.__offlineGuardBlocked = blocked ? report(url, 'XMLHttpRequest') : null;

        if (blocked) return;   // 不发请求，但保持对象可用（send 时补一个 error 事件）

        return nativeOpen.apply(this, arguments);
      };

      XhrProto.send = function () {
        if (this.__offlineGuardBlocked) {
          var self = this;

          // 异步补 error 事件：调用方只挂 onerror 时也得知道失败了。
          setTimeout(function () {
            var evt;
            try {
              evt = new Event('error');
            } catch (e) {
              evt = { type: 'error' };
            }

            try {
              if (typeof self.onerror === 'function') self.onerror(evt);
            } catch (e) { /* 调用方自己的错，不管 */ }

            try {
              self.dispatchEvent(evt);
            } catch (e) { /* 同上 */ }
          }, 0);

          return;
        }

        return nativeSend.apply(this, arguments);
      };
    }

    // -------------------------------------------- WebSocket / EventSource
    guardConstructor('WebSocket', 'WebSocket');
    guardConstructor('EventSource', 'EventSource');

    // ---------------------------------------------------------- sendBeacon
    if (global.navigator && typeof global.navigator.sendBeacon === 'function') {
      var nativeBeacon = global.navigator.sendBeacon.bind(global.navigator);
      global.navigator.sendBeacon = function (url, data) {
        if (isExternal(url)) {
          report(url, 'sendBeacon');
          return false;
        }

        return nativeBeacon(url, data);
      };
    }

    // ------------------------------------------------- 元素级 URL 属性
    guardUrlProperty(global.HTMLScriptElement, 'src', 'script.src');
    guardUrlProperty(global.HTMLLinkElement, 'href', 'link.href');
    guardUrlProperty(global.HTMLImageElement, 'src', 'img.src');
    guardUrlProperty(global.HTMLMediaElement, 'src', 'media.src');
    guardUrlProperty(global.HTMLSourceElement, 'src', 'source.src');

    return true;
  }

  global.OfflineGuard = {
    install: install,
    isInstalled: function () { return installed; },
    host: function () { return host; },
    violations: function () { return violations.slice(); },
    violationCount: function () { return violations.length; },
    /** 仅供排查：临时放行某个域名（面板里目前没有任何地方调它）。 */
    isExternal: isExternal
  };
})(window);
