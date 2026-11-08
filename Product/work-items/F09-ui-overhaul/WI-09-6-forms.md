# WI-09-6 — Form sections and field errors

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-5
- **Estimate:** L

## Goal

Data-entry forms are split into titled sections, show an error next to the field it belongs to, and keep their save actions in a sticky bar.

## Scope

- In: product edit, supplier edit, PO header edit, user edit, settings, stock move / bulk, category forms, change password; `_ValidationSummary`; a field-error helper; `site.css`; tests.
- Out: new validation rules or fields. The product picker and the PO line editor internals.

## Tasks

- [x] Add a field-error helper (`Html.FieldError(key)` or a partial) that renders `<small class="field-error" id="{key}-error">` when ModelState has errors for `key`, and a way to add `aria-invalid="true"` and `aria-describedby` to the control. No change when there are no errors.
- [x] `_ValidationSummary` renders `.alert.alert-error` with the existing message list.
- [x] Add `.form-row`, `.form-intro`, `.form-card`, `.field`, `.field-grid`, `.input-prefix`, `.hint`, `.action-bar` styles (from `/Dev/Overhaul`).
- [x] Product edit: Identity, Stock, Cost sections. Supplier edit: Contact, Notes. PO header: Supplier and dates, Notes. User edit and settings: sections that match their groups today. Other small forms get a single `.form-card`.
- [x] Actions move into `.action-bar`: form Cancel as a text button, one primary. The action bar is `position: sticky; bottom: 0`.
- [x] Controls keep their `name` values and ids so existing tests and model binding are unaffected.
- [x] Tests: posting an invalid product (empty name) returns the form containing `class="field-error"` and `aria-invalid="true"` next to the name input and the summary still lists it; a valid post still redirects; the same for supplier. Existing form tests pass unchanged.

## Acceptance criteria

- [x] An invalid save shows the message under the field and at the top.
- [x] Product, supplier and PO forms show their sections.
- [x] Sticky save bar is reachable on a long form at tablet width.
- [x] `dotnet test` is green.

## Notes

FR-7. Section text is short (one sentence). Don't wrap the form in more than one `<form>`.

### As built

- `Html.FieldError(key)` renders `<small class="field-error" id="Input_Name-error">` straight after the control; `Html.DescribedBy(key, hintId?)` gives the `aria-describedby` value (error id first, then an existing hint id). Both live in `Hosting/FormExtensions.cs` next to `AriaInvalid`. A valid form renders none of it.
- Sections: product (Identity, Stock, Cost), supplier (Contact, Notes), PO header (Supplier and dates, Notes), user (Account, Access, Password on create), settings (Company, Currency, Orders and stock). Section headings are `h2` with `aria-labelledby`.
- Change password and the stock move form (modal and page) show field errors but keep their own layout. The category forms and the reset-password dialog only change Cancel to a text button. Stock count and bulk keep their line-level errors in the summary.
- `_ValidationSummary` is `article.validation-summary.alert.alert-error` with the alert icon when signed in (the sprite is only present in the signed-in shell).
- The PO header form uses a static `.action-bar-static` instead of the sticky bar, because the line editor sits below it and a floating bar would cover it. Every other form uses the sticky bar.
- Existing ids and `name` values are unchanged; only `aria-describedby` was added.
