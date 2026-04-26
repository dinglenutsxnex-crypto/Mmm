window.VirtualScroll = (() => {
    let _dotNet = null;
    let _el = null;
    let _raf = null;
    let _pending = false;

    function init(dotNet, el) {
        cleanup();
        _dotNet = dotNet;
        _el = el;
        _el.addEventListener('scroll', onScroll, { passive: true });
    }

    function onScroll() {
        if (_pending) return;
        _pending = true;
        _raf = requestAnimationFrame(() => {
            _pending = false;
            if (!_el || !_dotNet) return;
            _dotNet.invokeMethodAsync('OnTableScroll', _el.scrollTop, _el.clientHeight);
        });
    }

    function scrollToTop() {
        if (_el) _el.scrollTop = 0;
    }

    function cleanup() {
        if (_el) _el.removeEventListener('scroll', onScroll);
        if (_raf) cancelAnimationFrame(_raf);
        _dotNet = null; _el = null; _raf = null; _pending = false;
    }

    return { init, scrollToTop, cleanup };
})();
