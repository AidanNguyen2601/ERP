// ===== Giao diện chung: menu (thu gọn, nhóm mở / đóng, tìm chức năng), yêu thích, gọi API tùy chọn =====
(function () {
    const ERP = window.ERP = window.ERP || {};
    ERP.favorites = ERP.favorites || [];
    ERP.urls = ERP.urls || {};
    const body = document.body;
    const isMobile = () => window.matchMedia("(max-width: 900px)").matches;
    const railMode = () => body.classList.contains("menu-collapsed") && !isMobile();

    // ---------- Tiện ích
    const store = {
        get(k, d) { try { const v = localStorage.getItem(k); return v == null ? d : JSON.parse(v); } catch { return d; } },
        set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* chế độ riêng tư */ } }
    };
    ERP.store = store;

    ERP.post = async function (url, data) {
        const token = document.querySelector('meta[name=csrf-token]')?.content || '';
        const res = await fetch(url, {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify(data ?? {})
        });
        let json = null; try { json = await res.json(); } catch { }
        if (!res.ok) throw new Error(json?.message || 'Không lưu được (mã ' + res.status + ').');
        return json;
    };

    ERP.toast = function (msg, kind) {
        const box = document.getElementById('toasts'); if (!box || !window.bootstrap) return;
        const el = document.createElement('div');
        el.className = 'toast align-items-center border-0 text-bg-' + (kind || 'dark');
        el.setAttribute('role', 'status');
        el.innerHTML = '<div class="d-flex"><div class="toast-body"></div><button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast"></button></div>';
        el.querySelector('.toast-body').textContent = msg;
        box.appendChild(el);
        el.addEventListener('hidden.bs.toast', () => el.remove());
        new bootstrap.Toast(el, { delay: 2500 }).show();
    };

    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const safeIcon = i => /^bi-[a-z0-9-]+$/.test(i || '') ? i : 'bi-star';
    const norm = s => (s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/đ/g, 'd').replace(/Đ/g, 'D').toLowerCase();

    // ---------- Yêu thích
    const same = (a, b) => (a || '').toLowerCase() === (b || '').toLowerCase();
    const isFav = url => ERP.favorites.some(f => same(f.url, url));

    function renderFavorites() {
        const list = ERP.favorites;
        document.querySelectorAll('[data-fav-list]').forEach(box => {
            const mode = box.getAttribute('data-fav-mode');
            const here = location.pathname + location.search;
            box.innerHTML = list.map((f, idx) => {
                const icon = safeIcon(f.icon), url = esc(f.url), title = esc(f.title), active = same(f.url, here) ? ' active' : '';
                const star = `<span class="fav-toggle on" role="button" tabindex="0" data-fav-url="${url}" data-fav-title="${title}" data-fav-icon="${icon}" title="Bỏ yêu thích"><i class="bi bi-star-fill"></i></span>`;
                if (mode === 'dropdown')
                    return `<a class="dropdown-item d-flex align-items-center gap-2${active}" href="${url}"><i class="bi ${icon}"></i><span class="flex-grow-1">${title}</span>${star}</a>`;
                if (mode === 'tiles')
                    return `<a class="tile fav-tile" href="${url}" draggable="true" data-idx="${idx}"><span class="ico"><i class="bi ${icon}"></i></span><span>${title}</span>${star}</a>`;
                return `<a class="nav-item${active}" href="${url}" title="${title}"><i class="bi ${icon}"></i><span class="txt">${title}</span>${star}</a>`;
            }).join('');
        });
        document.querySelectorAll('.fav-sec').forEach(s => s.hidden = list.length === 0);
        document.querySelectorAll('[data-fav-empty]').forEach(e => e.hidden = list.length > 0);
        document.querySelectorAll('[data-fav-url]').forEach(el => {
            const on = isFav(el.getAttribute('data-fav-url'));
            el.classList.toggle('on', on);
            const i = el.querySelector('i'); if (i) i.className = 'bi ' + (on ? 'bi-star-fill' : 'bi-star');
        });
    }
    ERP.renderFavorites = renderFavorites;

    async function toggleFavorite(el) {
        try {
            const r = await ERP.post(ERP.urls.toggleFavorite, {
                url: el.getAttribute('data-fav-url'), title: el.getAttribute('data-fav-title'), icon: el.getAttribute('data-fav-icon')
            });
            ERP.favorites = r.list || [];
            renderFavorites();
            ERP.toast(r.favorite ? 'Đã thêm vào yêu thích' : 'Đã bỏ khỏi yêu thích', r.favorite ? 'success' : 'secondary');
        } catch (e) { ERP.toast(e.message, 'danger'); }
    }
    ERP.saveFavorites = async function (list) {
        try { const r = await ERP.post(ERP.urls.saveFavorites, list); ERP.favorites = r.list || []; renderFavorites(); }
        catch (e) { ERP.toast(e.message, 'danger'); }
    };

    document.addEventListener('click', e => {
        const el = e.target.closest('[data-fav-url]');
        if (!el) return;
        e.preventDefault(); e.stopPropagation();
        toggleFavorite(el);
    }, true);
    document.addEventListener('keydown', e => {
        const el = e.target.closest?.('.fav-toggle');
        if (el && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); toggleFavorite(el); }
    });

    // Kéo thả sắp xếp ô yêu thích ở trang chủ
    document.addEventListener('dragstart', e => {
        const t = e.target.closest?.('.fav-tile'); if (!t) return;
        e.dataTransfer.setData('text/x-fav', t.getAttribute('data-idx')); e.dataTransfer.effectAllowed = 'move';
    });
    document.addEventListener('dragover', e => { if (e.target.closest?.('.fav-tile') && e.dataTransfer.types.includes('text/x-fav')) e.preventDefault(); });
    document.addEventListener('drop', e => {
        const t = e.target.closest?.('.fav-tile'); if (!t) return;
        const from = parseInt(e.dataTransfer.getData('text/x-fav'), 10), to = parseInt(t.getAttribute('data-idx'), 10);
        if (isNaN(from) || isNaN(to) || from === to) return;
        e.preventDefault();
        const list = ERP.favorites.slice(); const [m] = list.splice(from, 1); list.splice(to, 0, m);
        ERP.favorites = list; renderFavorites(); ERP.saveFavorites(list);
    });

    renderFavorites();

    // ---------- Menu dọc: nhóm mở / đóng (nhớ theo trình duyệt)
    const nav = document.querySelector('[data-side-nav]');
    const CLOSED_KEY = 'erp.nav.closed';
    if (nav) {
        const closed = new Set(store.get(CLOSED_KEY, []));
        nav.querySelectorAll('.nav-sec').forEach(sec => {
            const key = sec.getAttribute('data-group');
            if (closed.has(key) && !sec.classList.contains('has-active')) sec.classList.remove('open');
        });
        const saveClosed = () => store.set(CLOSED_KEY, [...nav.querySelectorAll('.nav-sec:not(.open)')].map(s => s.getAttribute('data-group')));

        nav.addEventListener('click', e => {
            const btn = e.target.closest('[data-group-toggle]'); if (!btn) return;
            if (railMode()) return; // thu gọn: dùng flyout
            btn.closest('.nav-sec').classList.toggle('open'); saveClosed();
        });

        document.querySelector('[data-menu-expand-all]')?.addEventListener('click', () => {
            const secs = [...nav.querySelectorAll('.nav-sec')];
            const anyOpen = secs.some(s => s.classList.contains('open') && !s.classList.contains('has-active'));
            secs.forEach(s => s.classList.toggle('open', !anyOpen || s.classList.contains('has-active')));
            saveClosed();
        });

        // Thu gọn menu thành thanh biểu tượng (lưu lên server)
        document.querySelector('[data-menu-collapse]')?.addEventListener('click', () => {
            const c = !body.classList.contains('menu-collapsed');
            body.classList.toggle('menu-collapsed', c);
            closeFlyouts();
            ERP.post(ERP.urls.collapse, { collapsed: c }).catch(() => { });
        });

        // Thu gọn: rê chuột / chạm vào biểu tượng nhóm để hiện danh sách chức năng bên cạnh
        let flyTimer = null;
        function closeFlyouts() { nav.querySelectorAll('.nav-sec.flying').forEach(s => s.classList.remove('flying')); }
        function openFlyout(sec) {
            if (!railMode()) return;
            clearTimeout(flyTimer); closeFlyouts();
            const box = sec.querySelector('.nav-items'); if (!box) return;
            const r = sec.getBoundingClientRect(), right = body.classList.contains('menu-right');
            sec.classList.add('flying');
            const h = box.offsetHeight;
            box.style.top = Math.max(8, Math.min(r.top, window.innerHeight - h - 8)) + 'px';
            if (right) { box.style.left = ''; box.style.right = (window.innerWidth - r.left) + 'px'; }
            else { box.style.right = ''; box.style.left = r.right + 'px'; }
        }
        nav.querySelectorAll('.nav-sec').forEach(sec => {
            sec.addEventListener('mouseenter', () => openFlyout(sec));
            sec.addEventListener('mouseleave', () => { flyTimer = setTimeout(closeFlyouts, 250); });
            sec.querySelector('[data-group-toggle]')?.addEventListener('click', () => {
                if (railMode()) sec.classList.contains('flying') ? closeFlyouts() : openFlyout(sec);
            });
        });
        document.addEventListener('click', e => { if (!e.target.closest('.nav-sec')) closeFlyouts(); });
        nav.addEventListener('scroll', closeFlyouts);

        // Tìm chức năng (không dấu)
        const search = document.querySelector('[data-menu-search]');
        search?.addEventListener('input', () => {
            const q = norm(search.value.trim());
            nav.classList.toggle('searching', q.length > 0);
            nav.querySelectorAll('.nav-sec').forEach(sec => {
                let any = false;
                sec.querySelectorAll('.nav-item').forEach(a => {
                    const hit = !q || norm(a.getAttribute('data-search') || a.textContent).includes(q);
                    a.classList.toggle('d-none', !hit); any = any || hit;
                });
                sec.classList.toggle('d-none', !!q && !any);
            });
        });
        search?.addEventListener('keydown', e => {
            if (e.key === 'Enter') { const a = nav.querySelector('.nav-sec:not(.d-none) .nav-item:not(.d-none)'); if (a) location.href = a.href; }
            if (e.key === 'Escape') { search.value = ''; search.dispatchEvent(new Event('input')); }
        });

        // Cuộn tới mục đang chọn
        nav.querySelector('.nav-item.active')?.scrollIntoView({ block: 'nearest' });
    }

    // ---------- Màn hình nhỏ: mở menu dạng ngăn kéo
    document.querySelector('[data-menu-open]')?.addEventListener('click', () => body.classList.add('menu-open'));
    document.querySelectorAll('[data-menu-close]').forEach(el => el.addEventListener('click', () => body.classList.remove('menu-open')));
})();
