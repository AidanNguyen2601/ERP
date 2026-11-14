// Xác nhận trước thao tác quan trọng: <form data-confirm="...">
document.addEventListener('submit', function (e) {
    const msg = e.target.getAttribute('data-confirm');
    if (msg && !confirm(msg)) e.preventDefault();
});

const fmt = n => Math.round(n).toLocaleString('vi-VN');
const num = el => parseFloat(el && el.value ? el.value : '0') || 0;

// ===== Bảng dòng chứng từ dùng chung (đơn mua / bán, hóa đơn, phiếu kho, BOM, bút toán) =====
document.querySelectorAll('table.line-editor').forEach(function (table) {
    const tbody = table.querySelector('tbody');
    const calc = table.getAttribute('data-calc') === '1';
    function recalc() {
        let sub = 0, vat = 0, debit = 0, credit = 0;
        tbody.querySelectorAll('tr.line').forEach((tr, i) => {
            tr.querySelector('.idx').textContent = i + 1;
            if (calc) {
                const q = num(tr.querySelector('[data-f=Quantity]')), p = num(tr.querySelector('[data-f=UnitPrice]'));
                const d = num(tr.querySelector('[data-f=DiscountPct]')), v = num(tr.querySelector('[data-f=VatRate]'));
                const amt = Math.round(q * p * (100 - d) / 100);
                const t = tr.querySelector('.total'); if (t) t.textContent = fmt(amt);
                sub += amt; vat += Math.round(amt * v / 100);
            }
            debit += num(tr.querySelector('[data-f=Debit]')); credit += num(tr.querySelector('[data-f=Credit]'));
        });
        const set = (k, v) => { const el = table.querySelector(`[data-sum=${k}]`); if (el) el.textContent = fmt(v); };
        set('sub', sub); set('vat', vat); set('total', sub + vat); set('debit', debit); set('credit', credit); set('diff', debit - credit);
        const diff = table.querySelector('[data-sum=diff]');
        if (diff) diff.closest('.diff').classList.toggle('text-danger', debit !== credit);
    }
    table.querySelector('[data-add-line]').addEventListener('click', () => {
        const tr = tbody.querySelector('tr.line').cloneNode(true);
        tr.querySelectorAll('input').forEach(i => i.value = i.getAttribute('data-f') === 'Quantity' ? '1' : (i.type === 'number' ? '0' : ''));
        tr.querySelectorAll('[data-f=Id]').forEach(i => i.value = '0');
        tr.querySelectorAll('select').forEach(s => { if (!s.classList.contains('vat')) s.selectedIndex = 0; });
        tbody.appendChild(tr); recalc();
    });
    tbody.addEventListener('click', e => {
        if (!e.target.classList.contains('remove-line')) return;
        if (tbody.querySelectorAll('tr.line').length > 1) e.target.closest('tr').remove();
        else e.target.closest('tr').querySelectorAll('input:not([data-f=Id])').forEach(i => i.value = '');
        recalc();
    });
    tbody.addEventListener('change', e => {
        if (!e.target.classList.contains('item-select')) return;
        const o = e.target.selectedOptions[0]; const tr = e.target.closest('tr');
        const p = tr.querySelector('[data-f=UnitPrice]'); if (p && o.value) p.value = o.getAttribute('data-price');
        const v = tr.querySelector('[data-f=VatRate]'); if (v && o.value) v.value = o.getAttribute('data-vat');
        const d = tr.querySelector('[data-f=Description]'); if (d && o.value && !d.value) d.value = o.getAttribute('data-name');
        recalc();
    });
    table.addEventListener('input', recalc);
    table.addEventListener('change', recalc);
    // Đặt tên Lines[i].Field liên tục trước khi gửi để model binding đọc đúng
    table.closest('form').addEventListener('submit', () => {
        tbody.querySelectorAll('tr.line').forEach((tr, i) =>
            tr.querySelectorAll('[data-f]').forEach(el => el.name = `Lines[${i}].${el.getAttribute('data-f')}`));
    });
    recalc();
});

// ===== Phiếu thu / chi: hiện trường theo loại nội dung =====
(function () {
    const purpose = document.getElementById('Purpose');
    if (!purpose) return;
    function sync() {
        const v = purpose.value;
        document.querySelectorAll('[data-show]').forEach(el => {
            el.style.display = el.getAttribute('data-show').split(',').includes(v) ? '' : 'none';
        });
    }
    purpose.addEventListener('change', sync); sync();
})();
