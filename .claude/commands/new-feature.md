---
description: Create a new feature spec and its three work items (db schema, api, frontend)
argument-hint: <feature-name>
---

Create a new feature named "$ARGUMENTS".

1. Read `Product/README.md` to find the next free feature number (`Fxx`) and `Product/architecture.md` for conventions.
2. Create `Product/Features/Fxx-<kebab-name>.md` from `Product/templates/feature-template.md`.
   Ask me clarifying questions about scope, roles, and edge cases before filling in requirements.
3. Create folder `Product/work-items/Fxx-<kebab-name>/` with three items from `Product/templates/work-item-template.md`:
   - `WI-xx-1-db-schema.md`
   - `WI-xx-2-api.md` (depends on 1)
   - `WI-xx-3-frontend.md` (depends on 2)
4. Add the feature and its work items to the tables in `Product/README.md` with status `Planned`.
5. If the feature adds entities, add them to the data model section of `Product/architecture.md`.
