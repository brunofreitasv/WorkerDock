# Documentation media

The diagrams are bundled SVG assets. Their `viewBox` scales with the article width; keep diagram labels short and provide the full explanation in the surrounding guide. Each embedded image needs localized alt text and a visible `Figure:` or `Figura:` caption.

The three PNG examples are captured from the local tenant app with synthetic API responses. They contain no real tenant identifiers, credentials, or destinations. The capture script overlays numbered labels on the relevant control.

## Preview and capture

From `frontend/`, start the tenant preview in one terminal:

```sh
npm run build --workspace @workerdock/tenant
npm run e2e:serve --workspace @workerdock/tenant
```

Then, in another terminal, capture the screens:

```sh
node scripts/capture-documentation.mjs
```

Set `DOCS_CAPTURE_BASE_URL` to use a different local preview URL. The script intercepts the relevant API calls with synthetic fixtures and writes `wizard-job.png`, `execution-timeline.png`, and `alert-rule.png` beside the SVG diagrams.

## Diagram sources

- `first-job.svg`: draft, validate, review, enable, and monitor.
- `execution-lifecycle.svg`: accepted execution states, attempts, and timeline.
- `input-choice.svg`: supported SQL and HTTP input binding choices.
- `schedule-flow.svg`: schedule configuration and UTC preview.
- `alert-delivery.svg`: rule activation through incident and outbox delivery.
