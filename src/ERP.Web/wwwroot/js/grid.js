// ===== Bảng dạng lưới: ẩn / hiện cột, kéo thả đổi thứ tự cột, kéo mép để chỉnh độ rộng =====
// Tự áp dụng cho mọi <table class="table"> trong nội dung có 1 dòng tiêu đề, không gộp ô, từ 3 cột trở lên.
// Bỏ qua: bảng nhập dòng chứng từ (.line-editor), bản in (.print-paper) và bảng có data-grid="off".
// Bố cục lưu theo người dùng trên server (Preferences/SaveGrid) — key = đường dẫn trang + thứ tự bảng.
(function () {
    const ERP = window.ERP = window.ERP || {};
    const saved = ERP.grids || {};
    const MIN_W = 44;

    const eligible = [...document.querySelectorAll('.content table.table')].filter(t =>
        !t.matches('.line-editor, [data-grid=off]') && !t.closest('.print-paper, [data-grid=off]') &&
        t.tHead && t.tHead.rows.length === 1 && t.tHead.rows[0].cells.length >= 3 &&
        [...t.tHead.rows[0].cells].every(c => c.colSpan === 1 && c.rowSpan === 1));

    eligible.forEach((table, n) => setup(table, n));

    function setup(table, n) {
        const head = table.tHead.rows[0];
        const count = head.cells.length;
        const labels = [...head.cells].map((c, i) => (c.textContent || '').trim().replace(/\s+/g, ' ') || (i === count - 1 ? 'Thao tác' : `Cột ${i + 1}`));
        const sig = labels.join('|');
        const key = (table.getAttribute('data-grid') || (location.pathname.toLowerCase().replace(/\/+$/, '') || '/') + '#' + n).slice(0, 180);

        let state = saved[key];
        if (!state || state.sig !== sig || !Array.isArray(state.order) || state.order.length !== count) state = null;
        state = state || { sig, order: labels.map((_, i) => i), hidden: [], widths: {} };
        state.hidden = state.hidden || []; state.widths = state.widths || {};

        // Đánh dấu cột gốc cho mọi ô ở các dòng đủ số cột (dòng gộp ô như "Không có dữ liệu" giữ nguyên)
        const rows = () => [...table.rows].filter(r => r.cells.length === count && [...r.cells].every(c => c.colSpan === 1));
        rows().forEach(r => [...r.cells].forEach((c, i) => { if (c.dataset.col === undefined) c.dataset.col = i; }));
        // Dòng thêm sau (JS khác) — đánh dấu theo vị trí hiện tại
        const tagNew = () => rows().forEach(r => { if (r.cells[0].dataset.col === undefined) [...r.cells].forEach((c, i) => c.dataset.col = state.order[i]); });

        table.classList.add('grid');
        const ths = () => [...head.cells];

        // ---------- Thanh công cụ
        const wrap = table.closest('.table-responsive') || table;
        const tools = document.createElement('div');
        tools.className = 'grid-tools no-print';
        tools.innerHTML = `
            <span class="grid-hint d-none d-md-inline"><i class="bi bi-arrows-move"></i> Kéo tiêu đề để đổi thứ tự · kéo mép cột để chỉnh độ rộng</span>
            <div class="dropdown">
                <button type="button" class="btn btn-sm btn-light grid-cols-btn" data-bs-toggle="dropdown" data-bs-auto-close="outside" data-fixed-dropdown>
                    <i class="bi bi-layout-three-columns"></i> Cột <span class="badge bg-secondary grid-hidden-count"></span></button>
                <div class="dropdown-menu dropdown-menu-end shadow grid-menu">
                    <div class="px-3 pb-1 small text-muted">Chọn cột hiển thị</div>
                    <div class="grid-col-list"></div>
                    <div class="dropdown-divider"></div>
                    <div class="d-flex gap-2 px-3">
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-act="all">Hiện tất cả</button>
                        <button type="button" class="btn btn-sm btn-outline-danger" data-act="reset">Mặc định</button>
                    </div>
                </div>
            </div>`;
        wrap.parentNode.insertBefore(tools, wrap);
        ERP.initFixedDropdowns?.(tools);
        const list = tools.querySelector('.grid-col-list');

        function renderChooser() {
            list.innerHTML = state.order.map(i => `
                <label class="dropdown-item d-flex align-items-center gap-2 py-1">
                    <input type="checkbox" class="form-check-input m-0" data-col="${i}" ${state.hidden.includes(i) ? '' : 'checked'}>
                    <span>${escapeHtml(labels[i])}</span></label>`).join('');
            const h = state.hidden.length;
            const badge = tools.querySelector('.grid-hidden-count');
            badge.textContent = h ? `${h} ẩn` : ''; badge.hidden = !h;
        }

        list.addEventListener('change', e => {
            const cb = e.target.closest('input[data-col]'); if (!cb) return;
            const i = +cb.dataset.col;
            if (cb.checked) state.hidden = state.hidden.filter(x => x !== i);
            else {
                if (state.hidden.length >= count - 1) { cb.checked = true; ERP.toast?.('Phải giữ lại ít nhất một cột.', 'warning'); return; }
                state.hidden.push(i);
            }
            apply(); save();
        });
        tools.querySelector('[data-act=all]').addEventListener('click', () => { state.hidden = []; apply(); save(); });
        tools.querySelector('[data-act=reset]').addEventListener('click', () => {
            state = { sig, order: labels.map((_, i) => i), hidden: [], widths: {} };
            apply(); save(true);
        });

        // ---------- Áp dụng bố cục
        function apply() {
            tagNew();
            rows().forEach(r => {
                const byCol = {}; [...r.cells].forEach(c => byCol[c.dataset.col] = c);
                state.order.forEach(i => { if (byCol[i]) r.appendChild(byCol[i]); });
                [...r.cells].forEach(c => c.classList.toggle('grid-hide', state.hidden.includes(+c.dataset.col)));
            });
            const fixed = Object.keys(state.widths).length > 0;
            table.classList.toggle('grid-fixed', fixed);
            if (fixed) {
                let total = 0;
                ths().forEach(th => {
                    const i = +th.dataset.col;
                    if (state.hidden.includes(i)) return;
                    const w = state.widths[i] || Math.max(MIN_W, Math.round(th.getBoundingClientRect().width)) || 120;
                    state.widths[i] = w; th.style.width = w + 'px'; total += w;
                });
                table.style.width = total + 'px';
            } else {
                ths().forEach(th => th.style.width = '');
                table.style.width = '';
            }
            renderChooser();
        }

        let timer = null;
        function save(reset) {
            ERP.grids = ERP.grids || {}; ERP.grids[key] = reset ? undefined : state;
            clearTimeout(timer);
            timer = setTimeout(() => {
                if (!ERP.post || !ERP.urls?.saveGrid) return;
                ERP.post(ERP.urls.saveGrid, { key, state: reset ? null : state }).catch(e => ERP.toast?.(e.message, 'danger'));
            }, 400);
        }

        // ---------- Chỉnh độ rộng: kéo tay nắm ở mép phải tiêu đề
        ths().forEach(th => {
            const h = document.createElement('span');
            h.className = 'col-resizer'; h.title = 'Kéo để chỉnh độ rộng · nhấp đúp để tự động';
            th.appendChild(h);
            h.addEventListener('pointerdown', e => {
                e.preventDefault(); e.stopPropagation();
                // Lần đầu: cố định độ rộng hiện tại của mọi cột để các cột khác không nhảy
                if (!Object.keys(state.widths).length)
                    ths().forEach(x => { if (!x.classList.contains('grid-hide')) state.widths[+x.dataset.col] = Math.round(x.getBoundingClientRect().width); });
                const i = +th.dataset.col, startX = e.clientX, startW = state.widths[i] || th.getBoundingClientRect().width;
                const rtl = getComputedStyle(table).direction === 'rtl';
                table.classList.add('grid-resizing'); h.setPointerCapture(e.pointerId);
                const move = ev => { state.widths[i] = Math.max(MIN_W, Math.round(startW + (rtl ? -1 : 1) * (ev.clientX - startX))); apply(); };
                const up = () => {
                    h.removeEventListener('pointermove', move); h.removeEventListener('pointerup', up); h.removeEventListener('pointercancel', up);
                    table.classList.remove('grid-resizing'); save();
                };
                h.addEventListener('pointermove', move); h.addEventListener('pointerup', up); h.addEventListener('pointercancel', up);
            });
            h.addEventListener('dblclick', e => { e.stopPropagation(); state.widths = {}; apply(); save(); });
            h.addEventListener('click', e => e.stopPropagation());
        });

        // ---------- Kéo thả tiêu đề để đổi thứ tự cột
        let dragCol = null;
        ths().forEach(th => {
            th.draggable = true;
            th.addEventListener('dragstart', e => {
                if (e.target.closest('.col-resizer')) { e.preventDefault(); return; }
                dragCol = +th.dataset.col; th.classList.add('grid-dragging');
                e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', labels[dragCol]);
            });
            th.addEventListener('dragend', () => { dragCol = null; ths().forEach(x => x.classList.remove('grid-dragging', 'drop-before', 'drop-after')); });
            th.addEventListener('dragover', e => {
                if (dragCol === null || +th.dataset.col === dragCol) return;
                e.preventDefault();
                const r = th.getBoundingClientRect(), after = e.clientX > r.left + r.width / 2;
                ths().forEach(x => x.classList.remove('drop-before', 'drop-after'));
                th.classList.add(after ? 'drop-after' : 'drop-before');
            });
            th.addEventListener('dragleave', () => th.classList.remove('drop-before', 'drop-after'));
            th.addEventListener('drop', e => {
                if (dragCol === null) return;
                e.preventDefault();
                const target = +th.dataset.col; if (target === dragCol) return;
                const after = th.classList.contains('drop-after');
                const order = state.order.filter(x => x !== dragCol);
                order.splice(order.indexOf(target) + (after ? 1 : 0), 0, dragCol);
                state.order = order; apply(); save();
            });
        });

        // Dòng thêm động (VD: JS thêm dòng) — áp lại bố cục
        if (table.tBodies[0]) new MutationObserver(() => { if (rows().some(r => r.cells[0].dataset.col === undefined)) apply(); })
            .observe(table.tBodies[0], { childList: true });

        apply();
    }

    function escapeHtml(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }
})();
