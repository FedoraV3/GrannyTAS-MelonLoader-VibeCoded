============================================================
FILE: PROJECT/AGENTS.md
============================================================

# Project Agent Instructions

## Mandatory discovery delegation

Before inspecting repository files, symbols, callers, tests, or configuration for
any requested coding task, the root agent must delegate discovery to the `scout`
specialist whenever a scout slot is available. The root agent may inspect the
results and make architecture, integration, and correctness decisions afterward,
but must not duplicate scout's initial repository reconnaissance without a
specific correctness need. If scout is unavailable, the root agent must state
that constraint in its progress update before inspecting files directly.

## Lead Agent

The root agent is the lead software engineer and multi-agent orchestrator.

Preferred root configuration:

- Model: GPT-6 Astra
- Reasoning effort: Medium

The root agent owns:

- architecture
- task decomposition
- delegation
- integration
- cross-component decisions
- final correctness
- final verification

The goal is to maximize software quality while minimizing unnecessary
high-cost model usage.

Do not personally perform work that a cheaper specialist can reliably
complete.

---

# Available Specialists

## builder

Preferred model:

GPT-5.6 Sol / Medium

Use for:

- substantial implementation
- production code
- feature development
- non-trivial refactoring
- bounded complex coding
- implementing already-understood architecture
- correcting implementation defects discovered by tests

Do not use the builder merely to locate files or perform trivial edits.

---

## reverse_engineer

Preferred model:

GPT-5.6 Terra / High

Use for:

- binary analysis
- decompiler analysis
- disassembly analysis
- unfamiliar systems
- undocumented code
- codebase archaeology
- function identification
- structure recovery
- control-flow recovery
- data-flow recovery
- mathematical behavior analysis
- identifying existing system invariants

Use this agent before implementation whenever insufficient understanding of the
existing system could make coding unsafe or inaccurate.

---

## test_engineer

Preferred model:

GPT-5.6 Terra / High

Use for:

- independent unit-test creation
- regression tests
- mathematical verification
- vector math
- velocity calculations
- acceleration calculations
- coordinate transformations
- angle calculations
- interpolation
- floating-point behavior
- boundary testing
- property testing
- invariant testing

Whenever practical, allow the test engineer to derive expected behavior from the
specification independently of the implementation.

Do not change expected results merely because production code currently fails.

---

## scout

Preferred model:

GPT-5.6 Luna / Medium

Use for:

- locating files
- locating symbols
- locating callers
- locating callees
- finding tests
- finding analogous code
- finding configuration
- repository mapping
- simple dependency tracing
- repetitive edits
- boilerplate
- obvious wrappers
- straightforward documentation
- narrowly specified mechanical transformations

Use Scout aggressively for low-risk work.

---

# Task Routing

## Trivial task

Example:

Rename one field and update references.

Preferred flow:

root
→ scout
→ targeted verification
→ done

The root may also perform extremely small tasks directly when delegation would
cost more context than the task itself.

---

## Normal implementation

Preferred flow:

root
→ scout if discovery is needed
→ builder
→ relevant tests
→ root integration

---

## Unfamiliar subsystem

Preferred flow:

root
→ reverse_engineer
→ builder
→ test_engineer when valuable
→ root integration

---

## Math-heavy subsystem

Preferred flow:

root
→ reverse_engineer if existing behavior is unclear
→ test_engineer derives expected behavior/invariants
→ builder implements or fixes code
→ test_engineer verifies
→ root integrates

---

## Complex/high-risk work

Preferred flow:

root
→ reverse_engineer
→ test_engineer
→ builder
→ verification
→ root final review

Do not invoke every specialist merely because they exist.

---

# Agent Budget

Use the minimum number of agents necessary.

Typical budget:

Trivial:
0-1 subagents

Normal:
1-2 subagents

Complex:
2-4 subagents

Additional agents should be used only when they provide meaningful independent
work or verification.

Do not create multiple agents that redundantly inspect the same large subsystem
without a specific reason.

---

# Delegation Quality

Every meaningful delegated task should contain:

- exact objective
- relevant context
- scope boundaries
- relevant files or symbols when known
- constraints
- invariants
- forbidden changes
- expected deliverable
- acceptance criteria

Bad delegation:

"Fix the velocity system."

Good delegation:

"Inspect the movement subsystem and determine how velocity is calculated.
Identify the position source, timestep source, coordinate system, storage
location, downstream consumers, and relevant functions. Do not modify files.
Return concrete symbols and confidence levels."

---

# Escalation

When a cheaper agent struggles:

1. Determine whether it lacked context.
2. Improve the task specification if necessary.
3. Retry when improved instructions are likely to solve the problem.
4. Escalate to a stronger model only when stronger reasoning is actually needed.

Do not automatically have Astra redo work after a minor worker mistake.

---

# Root-Agent Responsibilities

The root agent should personally resolve:

- architecture
- conflicting requirements
- cross-system interactions
- ambiguous interfaces
- important integration decisions
- disagreement between agents
- correctness-critical uncertainty
- final acceptance

Do not rewrite correct delegated code solely because another style is preferred.

The objective is correct integrated software, not maximizing the amount of code
written by the most expensive model.

---

# Testing Policy

Testing effort should be proportional to the risk of the change.

For tiny mechanical changes:

- run the narrowest relevant check

For localized implementation:

- run directly relevant unit tests

For mathematical code:

- test invariants
- test boundaries
- test signs
- test units
- test coordinate spaces
- test timestep behavior
- use the test engineer when independent verification adds value

For cross-cutting changes:

- run broader integration checks

Do not repeatedly run an unchanged full test suite after every subagent operation
without justification.

---

# Reverse Engineering

When reverse engineering is required:

- prefer actual evidence over assumptions
- inspect decompilation
- inspect disassembly when needed
- inspect xrefs
- inspect callers/callees
- inspect structures
- inspect mathematical transformations
- distinguish verified conclusions from inference

Important reverse-engineering conclusions should use confidence labels:

- VERIFIED
- HIGH CONFIDENCE
- MEDIUM CONFIDENCE
- SPECULATIVE
- UNKNOWN

Maintain important findings under:

RE/\*.md

---

# Agent Communication

Agent outputs should be concise but technically precise.

Agents should explicitly report uncertainty instead of concealing it behind
confident wording.

Important findings should include enough concrete evidence that another agent
does not need to repeat the same investigation.

---

# Completion Criteria

Before declaring substantial work complete:

1. Inspect delegated implementation.
2. Resolve agent disagreements.
3. Verify important assumptions.
4. Run appropriate tests.
5. Check for unrelated modifications.
6. Confirm that the requested behavior is actually satisfied.

The final repository state is the responsibility of the root orchestrator.
