// ===== Trình thiết kế báo cáo động: dựng form từ siêu dữ liệu nguồn dữ liệu, gom thành JSON gửi lên server =====
(function () {
    const META = window.RPT_META || [];
    const form = document.getElementById('rptForm');
    if (!form || !META.length) return;
    const $ = id => document.getElementById(id);
    let def = window.RPT_DEF || {};
    const byKey = Object.fromEntries(META.map(d => [d.key, d]));
    const AGGS = [['Sum', 'Tổng'], ['Count', 'Đếm dòng'], ['CountDistinct', 'Đếm không trùng'], ['Avg', 'Trung bình'], ['Min', 'Nhỏ nhất'], ['Max', 'Lớn nhất'], ['Ratio', 'Tỷ lệ % (A / B)']];
    const PRESETS = [['all', 'Toàn bộ thời gian'], ['today', 'Hôm nay'], ['thisMonth', 'Tháng này'], ['lastMonth', 'Tháng trước'], ['thisQuarter', 'Quý này'], ['lastQuarter', 'Quý trước'],
        ['thisYear', 'Năm nay'], ['lastYear', 'Năm trước'], ['last30', '30 ngày qua'], ['last90', '90 ngày qua'], ['last365', '12 tháng qua'], ['custom', 'Tùy chọn…']];
    const OPS = { text: [['eq', 'bằng'], ['ne', 'khác'], ['contains', 'chứa'], ['notcontains', 'không chứa'], ['startswith', 'bắt đầu bằng'], ['in', 'thuộc (a,b,c)'], ['empty', 'để trống'], ['notempty', 'có giá trị']],
        num: [['eq', '='], ['ne', '≠'], ['gt', '>'], ['ge', '≥'], ['lt', '<'], ['le', '≤']], date: [['eq', 'ngày'], ['ge', 'từ ngày'], ['le', 'đến ngày'], ['gt', 'sau ngày'], ['lt', 'trước ngày'], ['empty', 'để trống']] };

    const ds = () => byKey[def.dataset] || META[0];
    const fld = k => ds().fields.find(f => f.key === k);
    const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const opts = (items, sel) => items.map(([v, t]) => `<option value="${esc(v)}" ${v === sel ? 'selected' : ''}>${esc(t)}</option>`).join('');
    const fieldOpts = (list, sel, blank) => (blank ? `<option value="">${esc(blank)}</option>` : '') + list.map(f => `<option value="${esc(f.key)}" ${f.key === sel ? 'selected' : ''}>${esc(f.title)}</option>`).join('');
    const kindGroup = f => f.numeric ? 'num' : f.kind === 'Date' ? 'date' : 'text';

    function defaults(d) {
        const m = d.fields.find(f => f.additive);
        def.rows = d.fields.filter(f => f.kind === 'Text').slice(0, 1).map(f => f.key);
        def.measures = [m ? { field: m.key, agg: 'Sum' } : { field: d.fields[0].key, agg: 'Count' }];
        def.columns = d.fields.slice(0, 8).map(f => f.key);
        def.filters = []; def.sort = []; def.pivotColumn = null; def.top = null;
        def.datePreset = d.dateMode === 'None' ? 'all' : 'thisMonth';
    }

    // ---------- dựng giao diện
    function render() {
        def.rows ||= []; def.measures ||= []; def.columns ||= []; def.filters ||= []; def.sort ||= [];
        const d = ds();
        // nguồn dữ liệu (nhóm theo phân hệ)
        const groups = {}; META.forEach(x => (groups[x.group] ||= []).push(x));
        $('dsSelect').innerHTML = Object.entries(groups).map(([g, l]) => `<optgroup label="${esc(g)}">${l.map(x => `<option value="${esc(x.key)}" ${x.key === d.key ? 'selected' : ''}>${esc(x.title)}</option>`).join('')}</optgroup>`).join('');
        $('dsDesc').textContent = d.description || '';
        // thời gian
        $('dateBox').style.display = d.dateMode === 'None' ? 'none' : '';
        $('dateLabel').textContent = d.dateMode === 'AsOf' ? d.dateLabel + ' (mặc định hôm nay)' : d.dateLabel;
        const presets = d.dateMode === 'AsOf' ? PRESETS.filter(p => ['all', 'custom'].includes(p[0])) : PRESETS;
        if (!presets.some(p => p[0] === def.datePreset)) def.datePreset = presets[0][0];
        $('datePreset').innerHTML = opts(presets, def.datePreset);
        $('customDates').style.display = def.datePreset === 'custom' ? '' : 'none';
        $('dFrom').style.display = d.dateMode === 'AsOf' ? 'none' : ''; $('customDates').querySelector('span').style.display = d.dateMode === 'AsOf' ? 'none' : '';
        $('dFrom').value = (def.from || '').slice(0, 10); $('dTo').value = (def.to || '').slice(0, 10);
        // chế độ
        ($('modeDetail').checked = def.mode === 'detail'); $('modeGrouped').checked = def.mode !== 'detail';
        document.querySelectorAll('[data-mode]').forEach(el => el.style.display = el.dataset.mode === (def.mode === 'detail' ? 'detail' : 'grouped') ? '' : 'none');
        // nhóm theo
        const all = d.fields;
        $('rowsBox').innerHTML = def.rows.map((k, i) => `<div class="input-group input-group-sm"><select class="form-select" data-row="${i}">${fieldOpts(all, k)}</select><button type="button" class="btn btn-outline-danger" data-rm-row="${i}" title="Bỏ">✕</button></div>`).join('')
            || '<div class="small text-muted">Không nhóm – gộp tất cả thành một dòng.</div>';
        $('addRow').style.display = def.rows.length >= 4 ? 'none' : '';
        $('pivotSel').innerHTML = fieldOpts(all.filter(f => !def.rows.includes(f.key)), def.pivotColumn, '(Không dùng cột chéo)');
        // chỉ số
        $('measBox').innerHTML = def.measures.map((m, i) => {
            const numeric = all.filter(f => f.numeric); const countOnly = m.agg === 'Count' || m.agg === 'CountDistinct';
            return `<div class="rpt-meas"><select class="form-select form-select-sm" data-agg="${i}">${opts(AGGS, m.agg)}</select>
              ${m.agg === 'Count' ? '' : `<select class="form-select form-select-sm" data-mfield="${i}">${fieldOpts(countOnly ? all : numeric, m.field)}</select>`}
              ${m.agg === 'Ratio' ? `<span class="small">/</span><select class="form-select form-select-sm" data-mfield2="${i}">${fieldOpts(numeric, m.field2)}</select>` : ''}
              <input class="form-control form-control-sm" data-mtitle="${i}" placeholder="Tên cột (tùy chọn)" value="${esc(m.title || '')}">
              <button type="button" class="btn btn-sm btn-outline-danger" data-rm-meas="${i}" ${def.measures.length <= 1 ? 'disabled' : ''}>✕</button></div>`;
        }).join('');
        $('addMeas').style.display = def.measures.length >= 8 || def.pivotColumn ? 'none' : '';
        // cột chi tiết
        $('colsBox').innerHTML = all.map(f => `<label class="form-check form-check-inline small"><input class="form-check-input" type="checkbox" data-col="${esc(f.key)}" ${def.columns.includes(f.key) ? 'checked' : ''}> ${esc(f.title)}</label>`).join('');
        // bộ lọc
        $('fltBox').innerHTML = def.filters.map((f, i) => {
            const fd = fld(f.field) || all[0]; const g = kindGroup(fd); const ops = OPS[g];
            const noVal = f.op === 'empty' || f.op === 'notempty';
            return `<div class="rpt-flt"><select class="form-select form-select-sm" data-ffield="${i}">${fieldOpts(all, f.field)}</select>
              <select class="form-select form-select-sm" data-fop="${i}">${opts(ops, f.op)}</select>
              ${noVal ? '' : `<input class="form-control form-control-sm" data-fval="${i}" type="${g === 'date' ? 'date' : 'text'}" value="${esc(f.value || '')}" placeholder="Giá trị">`}
              <button type="button" class="btn btn-sm btn-outline-danger" data-rm-flt="${i}">✕</button></div>`;
        }).join('') || '<div class="small text-muted">Không có bộ lọc.</div>';
        // sắp xếp: khóa cột kết quả
        const sortKeys = def.mode === 'detail' ? def.columns.map(k => [k, fld(k)?.title || k])
            : [...def.rows.map(k => [k, fld(k)?.title || k]), ...def.measures.map((m, i) => ['m' + i, m.title || measTitle(m)])].concat(def.pivotColumn ? [['pv_total', 'Tổng']] : []);
        const cur = def.sort[0] || { key: '', desc: false };
        $('sortSel').innerHTML = `<option value="">(Mặc định)</option>` + opts(sortKeys, cur.key); $('sortDesc').checked = !!cur.desc;
        $('topN').value = def.top || ''; $('chartSel').value = def.chart || 'none'; $('showTotals').checked = def.showTotals !== false;
    }
    function measTitle(m) { const f = fld(m.field)?.title || m.field; return m.agg === 'Count' ? 'Số dòng' : m.agg === 'Sum' ? f : `${AGGS.find(a => a[0] === m.agg)?.[1] || ''} ${f}`; }

    // ---------- sự kiện
    form.addEventListener('change', e => {
        const t = e.target;
        if (t.id === 'dsSelect') { def.dataset = t.value; defaults(byKey[t.value]); }
        else if (t.id === 'datePreset') def.datePreset = t.value;
        else if (t.id === 'dFrom') def.from = t.value || null; else if (t.id === 'dTo') def.to = t.value || null;
        else if (t.name === 'mode') def.mode = t.value;
        else if (t.dataset.row !== undefined) def.rows[+t.dataset.row] = t.value;
        else if (t.id === 'pivotSel') { def.pivotColumn = t.value || null; if (def.pivotColumn) def.measures = def.measures.slice(0, 1); }
        else if (t.dataset.agg !== undefined) { const m = def.measures[+t.dataset.agg]; m.agg = t.value; if (t.value === 'Count') m.field = ds().fields[0].key; else if (!['Count', 'CountDistinct'].includes(t.value) && !fld(m.field)?.numeric) m.field = ds().fields.find(f => f.numeric)?.key; if (t.value === 'Ratio') m.field2 ||= ds().fields.filter(f => f.numeric)[1]?.key || m.field; }
        else if (t.dataset.mfield !== undefined) def.measures[+t.dataset.mfield].field = t.value;
        else if (t.dataset.mfield2 !== undefined) def.measures[+t.dataset.mfield2].field2 = t.value;
        else if (t.dataset.col !== undefined) { const k = t.dataset.col; def.columns = ds().fields.map(f => f.key).filter(x => x === k ? t.checked : def.columns.includes(x)); }
        else if (t.dataset.ffield !== undefined) { const f = def.filters[+t.dataset.ffield]; f.field = t.value; f.op = OPS[kindGroup(fld(t.value))][0][0]; f.value = ''; }
        else if (t.dataset.fop !== undefined) def.filters[+t.dataset.fop].op = t.value;
        else if (t.dataset.fval !== undefined) def.filters[+t.dataset.fval].value = t.value;
        else if (t.id === 'sortSel' || t.id === 'sortDesc') def.sort = $('sortSel').value ? [{ key: $('sortSel').value, desc: $('sortDesc').checked }] : [];
        else if (t.id === 'topN') def.top = t.value ? +t.value : null;
        else if (t.id === 'chartSel') def.chart = t.value;
        else if (t.id === 'showTotals') def.showTotals = t.checked;
        else return;
        render();
    });
    form.addEventListener('input', e => { const t = e.target; if (t.dataset.mtitle !== undefined) def.measures[+t.dataset.mtitle].title = t.value; if (t.dataset.fval !== undefined) def.filters[+t.dataset.fval].value = t.value; });
    form.addEventListener('click', e => {
        const t = e.target.closest('button'); if (!t) return;
        if (t.id === 'addRow') { const used = def.rows; const next = ds().fields.find(f => f.kind === 'Text' && !used.includes(f.key)) || ds().fields.find(f => !used.includes(f.key)); if (next) def.rows.push(next.key); }
        else if (t.dataset.rmRow !== undefined) def.rows.splice(+t.dataset.rmRow, 1);
        else if (t.id === 'addMeas') { const f = ds().fields.find(x => x.additive) || ds().fields[0]; def.measures.push({ field: f.key, agg: 'Sum' }); }
        else if (t.dataset.rmMeas !== undefined) def.measures.splice(+t.dataset.rmMeas, 1);
        else if (t.id === 'addFlt') { const f = ds().fields[0]; def.filters.push({ field: f.key, op: OPS[kindGroup(f)][0][0], value: '' }); }
        else if (t.dataset.rmFlt !== undefined) def.filters.splice(+t.dataset.rmFlt, 1);
        else return;
        e.preventDefault(); render();
    });
    // trước khi gửi (Chạy / Lưu / Xuất): ghi định nghĩa JSON vào trường ẩn
    form.addEventListener('submit', () => {
        def.mode = $('modeDetail').checked ? 'detail' : 'grouped';
        def.sort = $('sortSel').value ? [{ key: $('sortSel').value, desc: $('sortDesc').checked }] : [];
        $('defJson').value = JSON.stringify(def);
    });

    if (!def.dataset || !byKey[def.dataset]) { def.dataset = META[0].key; defaults(META[0]); }
    render();
})();
