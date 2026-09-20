// RemoteAccessHub 화면 판독 스크립트.
// Flutter 웹(HTML 렌더러) 화면에서 접근성 노드(flt-semantics)와 장면 텍스트(flt-paragraph)를 좌표와 함께 수집한다.
// 입력값(비밀번호·캡차 등)은 절대 읽지 않는다. 라벨과 텍스트만 수집하며 길이를 제한한다.
(function () {
  var t0 = (window.performance && performance.now) ? performance.now() : 0;
  var out = {
    url: location.href.split('?')[0].split('#')[0] + (location.hash || ''),
    title: document.title || '',
    readyState: document.readyState,
    flutter: false,
    placeholder: false,
    semanticsCount: 0,
    paragraphCount: 0,
    roots: 1,
    nodes: [],
    paragraphs: [],
    markers: { passwordInput: false, loginButton: false, logoutLabel: false, loadingOverlay: false, wakeButtons: 0, inputs: 0, dialogTexts: [] },
    error: null,
    elapsedMs: 0
  };
  var MAX_NODES = 500, MAX_PARAS = 500, MAX_LABEL = 160;

  // 실제 ipTIME 펌웨어(Flutter HTML 렌더러)에서는 flt-paragraph의 getBoundingClientRect가
  // 위치만 있고 크기가 0으로 보고된다. 그런 경우 자식 요소와 텍스트 범위로 실제 크기를 구한다.
  function rect(el) {
    var r0 = { x: 0, y: 0, w: 0, h: 0 };
    try {
      var r = el.getBoundingClientRect();
      r0 = { x: r.x, y: r.y, w: r.width, h: r.height };
      if (r0.w > 0 && r0.h > 0) return r0;
    } catch (e) { return r0; }
    var u = unionOfChildren(el);
    if (u) return u;
    var t = rangeRect(el);
    if (t) return t;
    return r0;
  }
  function unionOfChildren(el) {
    try {
      var kids = el.querySelectorAll('*');
      var x0 = Infinity, y0 = Infinity, x1 = -Infinity, y1 = -Infinity, found = false;
      for (var i = 0; i < kids.length && i < 30; i++) {
        var r = kids[i].getBoundingClientRect();
        if (r.width <= 0 || r.height <= 0) continue;
        found = true;
        if (r.x < x0) x0 = r.x;
        if (r.y < y0) y0 = r.y;
        if (r.x + r.width > x1) x1 = r.x + r.width;
        if (r.y + r.height > y1) y1 = r.y + r.height;
      }
      return found ? { x: x0, y: y0, w: x1 - x0, h: y1 - y0 } : null;
    } catch (e) { return null; }
  }
  function rangeRect(el) {
    try {
      var rg = document.createRange();
      rg.selectNodeContents(el);
      var r = rg.getBoundingClientRect();
      rg.detach && rg.detach();
      if (r && r.width > 0 && r.height > 0) return { x: r.x, y: r.y, w: r.width, h: r.height };
      return null;
    } catch (e) { return null; }
  }
  function visible(el) { var r = rect(el); return r.w > 0 && r.h > 0; }
  function compact(s) { return (s || '').replace(/\s+/g, ' ').trim(); }
  function ownText(el) {
    // 직접 텍스트 노드와 SPAN 자식만 읽는다(하위 flt-semantics의 라벨은 섞지 않음).
    var s = '';
    for (var i = 0; i < el.childNodes.length; i++) {
      var c = el.childNodes[i];
      if (c.nodeType === 3) s += c.textContent;
      else if (c.nodeType === 1 && c.tagName === 'SPAN') s += c.textContent;
    }
    return s;
  }
  function inputInfo(el) {
    var inp = null;
    for (var i = 0; i < el.children.length; i++) {
      var c = el.children[i];
      if (c.tagName === 'INPUT' || c.tagName === 'TEXTAREA') { inp = c; break; }
    }
    if (!inp) return '';
    return (inp.type || 'text').toLowerCase();
  }

  function walk(node, depth, parentIdx) {
    if (!node) return;
    if (node.nodeType === 1) {
      var tag = node.tagName;
      var myIdx = parentIdx;
      if (tag === 'FLT-SEMANTICS-PLACEHOLDER') out.placeholder = true;
      if (tag === 'FLUTTER-VIEW' || tag === 'FLT-GLASS-PANE') out.flutter = true;
      if (tag === 'FLT-SEMANTICS') {
        out.semanticsCount++;
        if (out.nodes.length < MAX_NODES) {
          var role = node.getAttribute('role') || '';
          var label = node.getAttribute('aria-label') || '';
          if (!label) label = ownText(node);
          var inputType = inputInfo(node);
          if (inputType === 'password' && visible(node)) out.markers.passwordInput = true;
          var n = {
            index: out.nodes.length,
            id: node.id || '',
            parent: parentIdx,
            role: role,
            label: compact(label).slice(0, MAX_LABEL),
            inputType: inputType,
            rect: rect(node),
            hidden: node.getAttribute('aria-hidden') === 'true',
            disabled: node.getAttribute('aria-disabled') === 'true',
            depth: depth
          };
          out.nodes.push(n);
          myIdx = n.index;
        }
      } else if (tag === 'FLT-PARAGRAPH') {
        out.paragraphCount++;
        if (out.paragraphs.length < MAX_PARAS) {
          var txt = compact(node.textContent);
          if (txt) out.paragraphs.push({ index: out.paragraphs.length, text: txt.slice(0, MAX_LABEL), rect: rect(node) });
        }
      } else if (tag === 'INPUT' || tag === 'TEXTAREA') {
        out.markers.inputs++;
        if ((node.type || '').toLowerCase() === 'password' && visible(node)) out.markers.passwordInput = true;
      } else if (tag === 'BUTTON' && !out.flutter) {
        // 일반 HTML(비-Flutter) 페이지 대비: 버튼을 접근성 노드처럼 기록
        if (out.nodes.length < MAX_NODES) {
          var b = { index: out.nodes.length, id: node.id || '', parent: parentIdx, role: 'button', label: compact(node.textContent).slice(0, MAX_LABEL), inputType: '', rect: rect(node), hidden: !visible(node), disabled: !!node.disabled, depth: depth };
          out.nodes.push(b);
          myIdx = b.index;
        }
      }
      if (node.shadowRoot) { out.roots++; walk(node.shadowRoot, depth + 1, myIdx); }
      var ch = node.children;
      for (var k = 0; k < ch.length; k++) walk(ch[k], depth + 1, myIdx);
    } else if (node.nodeType === 9 || node.nodeType === 11) {
      var cs = node.children;
      for (var j = 0; j < cs.length; j++) walk(cs[j], depth, parentIdx);
    }
  }

  try {
    walk(document, 0, -1);
    if (!out.flutter) {
      // 비-Flutter 페이지(또는 Flutter 부팅 전)에서는 body 텍스트 일부를 단락처럼 기록(길이 제한)
      var bodyText = compact(document.body ? document.body.innerText : '');
      if (bodyText && out.paragraphs.length === 0) out.paragraphs.push({ index: 0, text: bodyText.slice(0, MAX_LABEL), rect: { x: 0, y: 0, w: 1, h: 1 } });
    }
    var texts = [];
    for (var i = 0; i < out.paragraphs.length; i++) texts.push(out.paragraphs[i].text);
    for (var j = 0; j < out.nodes.length; j++) if (out.nodes[j].label) texts.push(out.nodes[j].label);
    out.markers.logoutLabel = texts.some(function (t) { return /로그아웃|logout/i.test(t); });
    out.markers.loginButton = out.nodes.some(function (n) { return n.role === 'button' && /^(로그인|login)$/i.test(n.label); })
      || out.paragraphs.some(function (p) { return /^(로그인|login)$/i.test(p.text); });
    out.markers.wakeButtons = out.nodes.filter(function (n) { return n.role === 'button' && /^PC\s*켜기$/.test(n.label); }).length;
    out.markers.loadingOverlay = texts.some(function (t) { return /로딩 중입니다/.test(t); });
    out.markers.dialogTexts = out.nodes.filter(function (n) { return /dialog/i.test(n.role); }).map(function (n) { return n.label; }).slice(0, 5);
  } catch (e) {
    out.error = String(e).slice(0, 200);
  }
  out.elapsedMs = Math.round(((window.performance && performance.now) ? performance.now() : 0) - t0);
  return JSON.stringify(out);
})();
