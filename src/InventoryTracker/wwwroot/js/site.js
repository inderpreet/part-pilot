// Application script: HTMX conventions from Product/architecture.md §4.
//  - `flash` HX-Trigger event  -> toast in #flash (auto-dismisses)
//  - `closeModal` HX-Trigger   -> closes <dialog id="modal">
//  - loading content into #modal opens it; Escape / backdrop click / [data-close-modal] close it
//  - 4xx/5xx handling so server error partials and 422 validation responses are actually shown
//  - <time data-local> is upgraded to the browser's local time zone
//  - product picker ([data-product-picker]): select a result, scanner-style Enter, keyboard navigation
(() => {
  'use strict';

  const DISMISS_MS = { default: 4000, error: 8000 };

  // ---------- Flash / toasts ----------
  function scheduleDismiss(toast) {
    if (toast.dataset.dismissScheduled) return;
    toast.dataset.dismissScheduled = '1';
    const isError = toast.classList.contains('flash-error');
    const remove = () => {
      toast.classList.add('flash-leaving');
      setTimeout(() => toast.remove(), 300);
    };
    toast.addEventListener('click', remove);
    setTimeout(remove, isError ? DISMISS_MS.error : DISMISS_MS.default);
  }

  function showFlash(message, level) {
    const area = document.getElementById('flash');
    if (!area || !message) return;
    const toast = document.createElement('article');
    toast.className = `flash flash-${level || 'success'}`;
    toast.setAttribute('role', level === 'error' ? 'alert' : 'status');
    toast.textContent = String(message); // textContent: never interpret server text as HTML
    area.appendChild(toast);
  }

  // Anything that lands in #flash (server partials, TempData toasts, showFlash) gets auto-dismissed.
  function watchFlashArea() {
    const area = document.getElementById('flash');
    if (!area) return;
    area.querySelectorAll('.flash').forEach(scheduleDismiss);
    new MutationObserver(records => {
      for (const record of records) {
        record.addedNodes.forEach(node => {
          if (node.nodeType !== 1) return;
          if (node.classList.contains('flash')) scheduleDismiss(node);
          node.querySelectorAll?.('.flash').forEach(scheduleDismiss);
        });
      }
    }).observe(area, { childList: true, subtree: true });
  }

  // HX-Trigger: {"flash": "Saved"}  => detail.value === "Saved"
  // HX-Trigger: {"flash": {"message": "Oops", "level": "error"}}  => detail.message / detail.level
  document.addEventListener('flash', event => {
    const detail = event.detail ?? {};
    const message = typeof detail === 'string' ? detail : (detail.message ?? detail.value);
    const level = typeof detail === 'object' ? detail.level : undefined;
    showFlash(message, level);
  });

  // ---------- Modal ----------
  const modal = () => document.getElementById('modal');

  function closeModal() {
    const dialog = modal();
    if (dialog?.open) dialog.close();
  }

  document.addEventListener('closeModal', closeModal);

  document.addEventListener('click', event => {
    const dialog = modal();
    if (!dialog?.open) return;
    // Backdrop click (the <dialog> itself is the target) or an explicit close control.
    if (event.target === dialog || event.target.closest?.('[data-close-modal]')) closeModal();
  });

  // Native <dialog> already closes on Escape; handling it explicitly keeps the behavior independent of browser quirks.
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') closeModal();
  });

  // Empty the dialog when it closes (Escape included) so stale forms never reappear.
  function bindModalClose() {
    modal()?.addEventListener('close', () => { modal().innerHTML = ''; });
  }

  document.addEventListener('htmx:afterSwap', event => {
    const dialog = modal();
    if (!dialog || event.detail.target !== dialog || dialog.innerHTML.trim() === '') return;
    if (!dialog.open) dialog.showModal();
    dialog.querySelector('[autofocus], input:not([type=hidden]):not([disabled]), select, textarea')?.focus();
  });

  // ---------- Error handling ----------
  // htmx ignores 4xx/5xx bodies by default. Swap them when they are meant to be shown:
  //  - 422: validation errors rendered by the server into the form/target
  //  - any error response carrying HX-Retarget (the shared error partial retargets itself to #flash)
  // Otherwise tell the user instead of silently doing nothing.
  document.addEventListener('htmx:beforeSwap', event => {
    const xhr = event.detail.xhr;
    if (xhr.status === 422 || (xhr.status >= 400 && xhr.getResponseHeader('HX-Retarget'))) {
      event.detail.shouldSwap = true;
      event.detail.isError = false;
    } else if (xhr.status >= 400) {
      showFlash(`The request failed (${xhr.status}). Please try again.`, 'error');
    }
  });

  document.addEventListener('htmx:sendError', () => showFlash('Could not reach the server. Check your connection.', 'error'));
  document.addEventListener('htmx:timeout', () => showFlash('The request timed out. Please try again.', 'error'));

  // ---------- Local time ----------
  function upgradeTimes(root) {
    (root || document).querySelectorAll('time[data-local]').forEach(el => {
      if (el.dataset.localDone) return;
      const date = new Date(el.getAttribute('datetime'));
      if (Number.isNaN(date.getTime())) return;
      el.textContent = el.dataset.local === 'date'
        ? date.toLocaleDateString()
        : date.toLocaleString([], { dateStyle: 'short', timeStyle: 'short' });
      el.dataset.localDone = '1';
    });
  }

  // ---------- Product picker (_ProductPicker) ----------
  // Markup: [data-product-picker] > input[data-picker-input] + input[data-picker-value] + [data-picker-results].
  // Picking a row stores its id in the hidden input and its label in the text input. Typing again clears the stored
  // id, so a stale selection can never be submitted. Enter (what a barcode scanner sends after the code) forces an
  // immediate lookup and picks the match when there is exactly one, or the first one is an exact SKU/barcode hit.
  const pickFirstFor = new WeakSet();

  function pickerParts(picker) {
    return {
      input: picker.querySelector('[data-picker-input]'),
      value: picker.querySelector('[data-picker-value]'),
      results: picker.querySelector('[data-picker-results]'),
    };
  }

  function choose(button) {
    const picker = button.closest('[data-product-picker]');
    if (!picker) return;
    const { input, value, results } = pickerParts(picker);
    value.value = button.dataset.id;
    input.value = button.dataset.label;
    input.setAttribute('aria-expanded', 'false');
    results.innerHTML = '';
    value.dispatchEvent(new Event('change', { bubbles: true }));
  }

  document.addEventListener('click', event => {
    const button = event.target.closest?.('[data-picker-select]');
    if (button) {
      choose(button);
      return;
    }
    // Click elsewhere closes any open dropdown.
    document.querySelectorAll('[data-product-picker]').forEach(picker => {
      if (!picker.contains(event.target)) {
        const { input, results } = pickerParts(picker);
        results.innerHTML = '';
        input.setAttribute('aria-expanded', 'false');
      }
    });
  });

  document.addEventListener('input', event => {
    const input = event.target.closest?.('[data-picker-input]');
    if (!input) return;
    const { value } = pickerParts(input.closest('[data-product-picker]'));
    if (value.value !== '') {
      value.value = '';
      value.dispatchEvent(new Event('change', { bubbles: true }));
    }
  });

  document.addEventListener('keydown', event => {
    const input = event.target.closest?.('[data-picker-input]');
    if (!input) return;
    const picker = input.closest('[data-product-picker]');
    const { results } = pickerParts(picker);

    if (event.key === 'Enter') {
      event.preventDefault(); // never submit the surrounding form from the picker
      if (!input.value.trim()) return;
      pickFirstFor.add(picker);
      if (window.htmx) window.htmx.trigger(input, 'lookup-now');
    } else if (event.key === 'Escape') {
      results.innerHTML = '';
      input.setAttribute('aria-expanded', 'false');
    } else if (event.key === 'ArrowDown') {
      results.querySelector('[data-picker-select]')?.focus();
      event.preventDefault();
    }
  });

  // Arrow keys / Escape while focus is inside the results list.
  document.addEventListener('keydown', event => {
    const item = event.target.closest?.('[data-picker-select]');
    if (!item) return;
    const picker = item.closest('[data-product-picker]');
    const { input } = pickerParts(picker);
    const row = item.closest('li');
    if (event.key === 'ArrowDown') {
      row?.nextElementSibling?.querySelector('[data-picker-select]')?.focus();
      event.preventDefault();
    } else if (event.key === 'ArrowUp') {
      const previous = row?.previousElementSibling?.querySelector('[data-picker-select]');
      (previous ?? input).focus();
      event.preventDefault();
    } else if (event.key === 'Escape') {
      input.focus();
    }
  });

  document.addEventListener('htmx:afterSwap', event => {
    const results = event.detail.target;
    if (!results?.matches?.('[data-picker-results]')) return;
    const picker = results.closest('[data-product-picker]');
    const { input } = pickerParts(picker);
    const items = results.querySelectorAll('[data-picker-select]');
    input.setAttribute('aria-expanded', items.length > 0 ? 'true' : 'false');

    if (pickFirstFor.delete(picker) && items.length > 0 && (items.length === 1 || items[0].dataset.exact === 'true')) {
      choose(items[0]);
    }
  });

  // ---------- Boot ----------
  function init() {
    watchFlashArea();
    bindModalClose();
    upgradeTimes(document);
    if (window.htmx) window.htmx.onLoad(upgradeTimes); // also covers swapped-in content
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
  else init();
})();
