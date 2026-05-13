// Entry point for the Cogworks.UmbracoAI.AgentMemory frontend bundle.
// Bellissima reads NAMED exports from this module (per Story 0.A § Locked
// decision (c)). The side-effect import below MUST come BEFORE the `manifests`
// export so `customElements.define("cogworks-agent-feedback", ...)` has run
// before Bellissima resolves the `elementName` lookup.

import "./feedback-widget/cogworks-agent-feedback.element.js";

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
];

// Future entry points (uncomment as components ship):
// import "./run-list/cogworks-agent-run-list.element.js";
// import "./feedback-dashboard/cogworks-feedback-dashboard.element.js";
