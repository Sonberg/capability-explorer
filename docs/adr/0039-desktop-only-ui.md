# Design the Explorer UI for desktop and laptop use

* Status: accepted
* Scope: Explorer UI target platforms; no access-control or deployment change
* Date: 2026-11-10
* Decision owner: project operator
* Coordination: [issue #29](https://github.com/Sonberg/capability-explorer/issues/29)
* Decision record: [operator direction and scope](https://github.com/Sonberg/capability-explorer/issues/29#issuecomment-6769414442)

On 2026-11-10 the operator directed: "there will be no mobile access!" and
requested this decision record. Accepted scope: Explorer UI desktop/laptop
target; no access-control or deployment change. This does not establish
implementation or accessibility qualification.

## Context and problem statement

The operator has repeatedly directed that the Explorer will not be accessed
on mobile devices and requested a durable decision record. Issue #29
previously required a mobile navigation drawer and mobile-device testing.
Those requirements conflict with the intended platform.

## Decision drivers

Focus design and qualification on the actual desktop and laptop users. Avoid
repeated mobile-specific design and test work. Preserve accessibility,
including keyboard and screen-reader access and desktop browser zoom.

## Considered options

1. Design and qualify the Explorer for desktop and mobile devices: rejected
   because mobile access is outside the operator's stated scope.
2. Design and qualify for desktop and laptop use only: selected.

## Decision outcome

The Explorer UI targets desktop and laptop use, including large displays.
Mobile devices, including phones and tablets, are not supported target
platforms. Do not require mobile-specific layouts, phone navigation drawers,
touch-first controls or mobile-device qualification for this UI.

This decision supersedes only issue #29's mobile-device design, drawer and
mobile-device testing requirements. Its other task, navigation, evidence,
recovery and accessibility requirements remain in force. A narrow CSS
viewport caused by desktop window resizing or browser zoom is not evidence
of mobile-device use. Continue the applicable desktop zoom and reflow checks,
including the existing 200% and applicable 400%/320-CSS-pixel checks, and
report exceptions and actual results.

This platform decision does not authorize user-agent blocking, network
exposure, authentication changes or a new access-control mechanism. It does
not claim that the current UI is accessible or that a design is implemented.
Supporting mobile devices in the future requires a new explicit decision.

## Consequences

Design, testing and maintenance can focus on the intended desktop workflow.
Users on unsupported mobile devices have no compatibility guarantee.
Desktop accessibility work remains necessary and is not replaced by a
desktop-only target.

## Validation

Review UI briefs, acceptance criteria and design-tool platform settings
against this decision. Check that they do not add mobile-specific deliverables
and that desktop keyboard, screen-reader, zoom and applicable reflow checks
remain explicit. This record establishes the platform decision, not evidence
that those checks have passed.
