// Entry point for the Umbraco.Community.AiAgentMemory frontend bundle.
// Bellissima reads NAMED exports from this module (per Story 0.A § Locked
// decision (c)). The side-effect import below MUST come BEFORE the `manifests`
// export so `customElements.define("cogworks-agent-feedback", ...)` has run
// before Bellissima resolves the `elementName` lookup.

import "./feedback-widget/cogworks-agent-feedback.element.js";

// Story 4.9 — Memory Learning Wall dashboard.
// Dynamic-import form (`element: () => import(...)`) is the canonical
// Bellissima dashboard pattern per Task 0a empirical findings against
// Umbraco.AI's own Welcome dashboard manifest. The element module is NOT
// side-effect imported here — Bellissima resolves + registers the custom
// element when the dashboard is first activated, keeping the modal-only
// boot path lean.

// Story 2.3 — Strategy B modal-replacement, locked at Task 0 against Automate
// 0.1.0--preview.374 (`Ua.Modal.RunDetail`). Weight 10000 sits well above any
// plausible Automate default — Bellissima's longest-prefix-wins / highest-weight
// resolution picks our extension over the upstream one when both register the
// same alias.
export const manifests: Array<UmbExtensionManifest> = [
  {
    type: "modal",
    alias: "Ua.Modal.RunDetail",
    name: "Cogworks Agent Feedback Modal",
    elementName: "cogworks-agent-feedback",
    weight: 10000,
  },
  // Story 4.9 — Memory Learning Wall dashboard. Mounts under the existing
  // Umbraco.AI section (alias `'ai'`) per Task 0a contract probe against the
  // canonical Umbraco.AI Welcome-dashboard shape at
  // `Umbraco.AI/.../Client/src/section/dashboard/manifests.ts`. The condition
  // gates the dashboard to the AI section only — adopters running the package
  // without Umbraco.AI installed will see neither the section nor the
  // dashboard (graceful absence; no boot-time error).
  //
  // `as unknown as UmbExtensionManifest`: the `UmbExtensionManifest` union
  // discriminates by `type` literal but TS doesn't always narrow the array
  // literal correctly across heterogeneous entries (modal + dashboard); the
  // cast disambiguates to the `ManifestDashboard` shape from
  // `@umbraco-cms/backoffice/dashboard` (verified at Task 0a).
  {
    type: "dashboard",
    alias: "Cogworks.AgentMemory.Dashboard.MemoryWall",
    name: "Memory Learning Wall",
    element: () => import("./memory-wall/cogworks-memory-wall.element.js"),
    weight: 100,
    meta: {
      label: "Memory Learning Wall",
      pathname: "memory-learning-wall",
    },
    conditions: [
      {
        alias: "Umb.Condition.SectionAlias",
        match: "ai",
      },
    ],
  } as unknown as UmbExtensionManifest,
];
