# Research: provenienza e matrice bidirezionale
**Natura:** proiezione documentale per audit, autorizzata il 2026-10-14. Non e un output di speckit-specify/plan/tasks e non autorizza implementazione o passaggio di gate.

**Autorita:** i due documenti sorgente prevalgono. Estratti e copie conservano testo, vincoli e stati originali. Alias e relazioni di audit non sono nuove decisioni. A53/A54 rimangono proposte tecniche.

**Tracciabilita:** [research.md](research.md) contiene hash, provenienze, matrici nei due sensi, lacune e validazione. I link relativi dentro le copie integrali sono testo originale: vanno risolti nella directory dei sorgenti. Per navigare usare i link di provenienza.
## Fonti

SHA-256 calcolati sui byte originali, encoding e terminatori inclusi.

| Fonte | Versione | SHA-256 | Copia integrale |
|---|---|---|---|
| [AI-Project-Documentation-MVP-Plan.md](../../AI-Project-Documentation-MVP-Plan.md) | 2.2 | `fa5304cfe0b7c1dbb0c3b55d6e7e54079e2b2f94b8968ad512b250aa4b453ae3` | plan.md, SOURCE_SNAPSHOT_BEGIN/END |
| [AI-Project-Documentation-Tasks.md](../../AI-Project-Documentation-Tasks.md) | 1.2 | `879983c096c9a675b38013d1ac77594e456960dea53a48cbb74a73f73cb9480d` | tasks.md, SOURCE_SNAPSHOT_BEGIN/END |

## Regole della matrice

Identita e alias sono esatti. Tutte le relazioni decisione-task sono **inferite** dal contenuto dei record, descrizioni e AC: non sono riferimenti espliciti originari, non certificano copertura completa e non cambiano dipendenze. La riga forward cita la decisione, la reverse cita il task e i suoi AC. FR comprende record tecnici/governance non atomici. Stati dal piano; A53/A54 non hanno FR. Relazioni gate-task sono appartenenza alla milestone, non prova di soddisfacimento. Q resta namespace distinto.

## Matrice decisioni / requisiti -> task

| ID originale | Alias FR | Stato originale | Task associati (inferenza) | Provenienza |
|---|---|---|---|---|
| D01 | FR-001 | Approvata | M1-06 (T013), M2-01 (T017), M2-07 (T023) | [AI-Project-Documentation-MVP-Plan.md:L27](../../AI-Project-Documentation-MVP-Plan.md#L27) |
| D02 | FR-002 | Approvata | M2-01 (T017), M2-05 (T021), M2-08 (T024), M2-09 (T025) | [AI-Project-Documentation-MVP-Plan.md:L28](../../AI-Project-Documentation-MVP-Plan.md#L28) |
| D03 | FR-003 | Approvata | M0-05 (T005), M1-04 (T011), M1-05 (T012) | [AI-Project-Documentation-MVP-Plan.md:L29](../../AI-Project-Documentation-MVP-Plan.md#L29) |
| D04 | FR-004 | Approvata | M0-02 (T002), M1-06 (T013), M2-02 (T018) | [AI-Project-Documentation-MVP-Plan.md:L30](../../AI-Project-Documentation-MVP-Plan.md#L30) |
| D05 | FR-005 | Approvata | M0-01 (T001), M3-01 (T026), M5-01 (T047), M5-02 (T048) | [AI-Project-Documentation-MVP-Plan.md:L31](../../AI-Project-Documentation-MVP-Plan.md#L31) |
| D06 | FR-006 | Approvata | M4-02 (T039), M4-08 (T045) | [AI-Project-Documentation-MVP-Plan.md:L32](../../AI-Project-Documentation-MVP-Plan.md#L32) |
| D07 | FR-007 | Approvata | M0-02 (T002), M2-02 (T018), M2-03 (T019) | [AI-Project-Documentation-MVP-Plan.md:L33](../../AI-Project-Documentation-MVP-Plan.md#L33) |
| D08 | FR-008 | Approvata | M0-02 (T002), M2-02 (T018), M4-07 (T044) | [AI-Project-Documentation-MVP-Plan.md:L34](../../AI-Project-Documentation-MVP-Plan.md#L34) |
| D09 | FR-009 | Approvata | M0-05 (T005), M3-05 (T030), M3-06 (T031) | [AI-Project-Documentation-MVP-Plan.md:L35](../../AI-Project-Documentation-MVP-Plan.md#L35) |
| D10 | FR-010 | Approvata | M0-01 (T001), M1-02 (T009) | [AI-Project-Documentation-MVP-Plan.md:L36](../../AI-Project-Documentation-MVP-Plan.md#L36) |
| D11 | FR-011 | Approvata | M1-02 (T009) | [AI-Project-Documentation-MVP-Plan.md:L37](../../AI-Project-Documentation-MVP-Plan.md#L37) |
| D12 | FR-012 | Approvata | M1-04 (T011), M1-05 (T012) | [AI-Project-Documentation-MVP-Plan.md:L38](../../AI-Project-Documentation-MVP-Plan.md#L38) |
| D13 | FR-013 | Approvata | M2-01 (T017), M2-04 (T020), M2-07 (T023) | [AI-Project-Documentation-MVP-Plan.md:L39](../../AI-Project-Documentation-MVP-Plan.md#L39) |
| D14 | FR-014 | Approvata | M2-01 (T017), M2-07 (T023), M2-08 (T024) | [AI-Project-Documentation-MVP-Plan.md:L40](../../AI-Project-Documentation-MVP-Plan.md#L40) |
| D15 | FR-015 | Approvata | M3-05 (T030) | [AI-Project-Documentation-MVP-Plan.md:L41](../../AI-Project-Documentation-MVP-Plan.md#L41) |
| D16 | FR-016 | Approvata | M3-05 (T030) | [AI-Project-Documentation-MVP-Plan.md:L42](../../AI-Project-Documentation-MVP-Plan.md#L42) |
| D17 | FR-017 | Approvata | M1-03 (T010), M1-07 (T014), M2-02 (T018) | [AI-Project-Documentation-MVP-Plan.md:L43](../../AI-Project-Documentation-MVP-Plan.md#L43) |
| D18 | FR-018 | Approvata | M3-02 (T027), M3-07 (T032), M3-09 (T034), M3-10 (T035), M4-06 (T043) | [AI-Project-Documentation-MVP-Plan.md:L44](../../AI-Project-Documentation-MVP-Plan.md#L44) |
| D19 | FR-019 | Approvata | M3-04 (T029), M3-07 (T032) | [AI-Project-Documentation-MVP-Plan.md:L45](../../AI-Project-Documentation-MVP-Plan.md#L45) |
| D20 | FR-020 | Approvata | M0-03 (T003), M4-03 (T040), M4-05 (T042) | [AI-Project-Documentation-MVP-Plan.md:L46](../../AI-Project-Documentation-MVP-Plan.md#L46) |
| D21 | FR-021 | Approvata | M0-03 (T003), M1-03 (T010), M4-01 (T038), M4-02 (T039), M4-07 (T044) | [AI-Project-Documentation-MVP-Plan.md:L47](../../AI-Project-Documentation-MVP-Plan.md#L47) |
| D22 | FR-022 | Approvata | M0-03 (T003), M4-07 (T044), M4-08 (T045) | [AI-Project-Documentation-MVP-Plan.md:L48](../../AI-Project-Documentation-MVP-Plan.md#L48) |
| D23 | FR-023 | Approvata | M4-02 (T039) | [AI-Project-Documentation-MVP-Plan.md:L49](../../AI-Project-Documentation-MVP-Plan.md#L49) |
| D24 | FR-024 | Approvata | M4-02 (T039), M4-08 (T045) | [AI-Project-Documentation-MVP-Plan.md:L50](../../AI-Project-Documentation-MVP-Plan.md#L50) |
| D25 | FR-025 | Approvata | M0-01 (T001), M3-01 (T026), M5-01 (T047) | [AI-Project-Documentation-MVP-Plan.md:L51](../../AI-Project-Documentation-MVP-Plan.md#L51) |
| D26 | FR-026 | Approvata | M1-07 (T014), M1-09 (T016), M3-01 (T026), M3-02 (T027), M3-07 (T032), M3-09 (T034), M3-10 (T035) | [AI-Project-Documentation-MVP-Plan.md:L52](../../AI-Project-Documentation-MVP-Plan.md#L52) |
| D27 | FR-027 | Approvata | M4-05 (T042), M4-08 (T045) | [AI-Project-Documentation-MVP-Plan.md:L53](../../AI-Project-Documentation-MVP-Plan.md#L53) |
| D28 | FR-028 | Approvata | M0-02 (T002), M0-04 (T004), M2-03 (T019), M2-06 (T022), M3-03 (T028), M4-04 (T041), M4-07 (T044), M4-09 (T046) | [AI-Project-Documentation-MVP-Plan.md:L54](../../AI-Project-Documentation-MVP-Plan.md#L54) |
| D29 | FR-029 | Approvata | M4-06 (T043) | [AI-Project-Documentation-MVP-Plan.md:L55](../../AI-Project-Documentation-MVP-Plan.md#L55) |
| D30 | FR-030 | Approvata | M1-05 (T012), M2-05 (T021) | [AI-Project-Documentation-MVP-Plan.md:L56](../../AI-Project-Documentation-MVP-Plan.md#L56) |
| D31 | FR-031 | Approvata | M2-05 (T021), M3-03 (T028), M3-12 (T037), M4-09 (T046) | [AI-Project-Documentation-MVP-Plan.md:L57](../../AI-Project-Documentation-MVP-Plan.md#L57) |
| D32 | FR-032 | Approvata | M0-04 (T004), M2-05 (T021), M2-08 (T024), M2-09 (T025) | [AI-Project-Documentation-MVP-Plan.md:L58](../../AI-Project-Documentation-MVP-Plan.md#L58) |
| D33 | FR-033 | Approvata | M4-04 (T041), M4-08 (T045) | [AI-Project-Documentation-MVP-Plan.md:L59](../../AI-Project-Documentation-MVP-Plan.md#L59) |
| D34 | FR-034 | Approvata | M3-01 (T026), M5-02 (T048) | [AI-Project-Documentation-MVP-Plan.md:L60](../../AI-Project-Documentation-MVP-Plan.md#L60) |
| D35 | FR-035 | Approvata | M0-06 (T006), M2-06 (T022), M6-02 (T056) | [AI-Project-Documentation-MVP-Plan.md:L61](../../AI-Project-Documentation-MVP-Plan.md#L61) |
| D36 | FR-036 | Approvata | M0-04 (T004), M2-03 (T019), M2-05 (T021), M2-06 (T022), M2-09 (T025) | [AI-Project-Documentation-MVP-Plan.md:L62](../../AI-Project-Documentation-MVP-Plan.md#L62) |
| D37 | FR-037 | Approvata | M2-03 (T019), M2-04 (T020) | [AI-Project-Documentation-MVP-Plan.md:L63](../../AI-Project-Documentation-MVP-Plan.md#L63) |
| D38 | FR-038 | Approvata | M3-06 (T031), M2-01 (T017) | [AI-Project-Documentation-MVP-Plan.md:L64](../../AI-Project-Documentation-MVP-Plan.md#L64) |
| D39 | FR-039 | Approvata | M0-01 (T001), M0-05 (T005), M3-01 (T026), M5-01 (T047), M5-08 (T054) | [AI-Project-Documentation-MVP-Plan.md:L65](../../AI-Project-Documentation-MVP-Plan.md#L65) |
| D40 | FR-040 | Approvata | M0-05 (T005), M5-05 (T051), M5-06 (T052), M6-01 (T055) | [AI-Project-Documentation-MVP-Plan.md:L66](../../AI-Project-Documentation-MVP-Plan.md#L66) |
| D41 | FR-041 | Approvata | M1-08 (T015), M5-08 (T054) | [AI-Project-Documentation-MVP-Plan.md:L67](../../AI-Project-Documentation-MVP-Plan.md#L67) |
| D42 | FR-042 | Approvata | M0-05 (T005), M5-04 (T050), M6-03 (T057) | [AI-Project-Documentation-MVP-Plan.md:L68](../../AI-Project-Documentation-MVP-Plan.md#L68) |
| D43 | FR-043 | Approvata | M0-06 (T006), M1-08 (T015), M2-08 (T024), M3-08 (T033), M4-08 (T045), M5-08 (T054), M6-04 (T058) | [AI-Project-Documentation-MVP-Plan.md:L69](../../AI-Project-Documentation-MVP-Plan.md#L69) |
| D44 | FR-044 | Approvata | M1-02 (T009), M1-07 (T014), M5-03 (T049) | [AI-Project-Documentation-MVP-Plan.md:L70](../../AI-Project-Documentation-MVP-Plan.md#L70) |
| D45 | FR-045 | Approvata | M1-07 (T014), M3-02 (T027), M3-09 (T034), M4-05 (T042) | [AI-Project-Documentation-MVP-Plan.md:L71](../../AI-Project-Documentation-MVP-Plan.md#L71) |
| D46 | FR-046 | Approvata | M5-07 (T053) | [AI-Project-Documentation-MVP-Plan.md:L72](../../AI-Project-Documentation-MVP-Plan.md#L72) |
| D47 | FR-047 | Approvata | M4-03 (T040), M4-05 (T042) | [AI-Project-Documentation-MVP-Plan.md:L73](../../AI-Project-Documentation-MVP-Plan.md#L73) |
| D48 | FR-048 | Approvata | M0-05 (T005), M1-04 (T011) | [AI-Project-Documentation-MVP-Plan.md:L74](../../AI-Project-Documentation-MVP-Plan.md#L74) |
| D49 | FR-049 | Approvata | M1-06 (T013), M2-02 (T018) | [AI-Project-Documentation-MVP-Plan.md:L80](../../AI-Project-Documentation-MVP-Plan.md#L80) |
| D50 | FR-050 | Approvata | M4-01 (T038), M4-03 (T040), M4-07 (T044) | [AI-Project-Documentation-MVP-Plan.md:L81](../../AI-Project-Documentation-MVP-Plan.md#L81) |
| D51 | FR-051 | Approvata | M0-03 (T003), M1-01 (T008), M1-05 (T012), M4-03 (T040) | [AI-Project-Documentation-MVP-Plan.md:L82](../../AI-Project-Documentation-MVP-Plan.md#L82) |
| D52 | FR-052 | Approvata | M1-01 (T008), M1-08 (T015) | [AI-Project-Documentation-MVP-Plan.md:L83](../../AI-Project-Documentation-MVP-Plan.md#L83) |
| D53 | FR-053 | Approvata | M1-02 (T009), M1-08 (T015), M5-03 (T049), M6-01 (T055) | [AI-Project-Documentation-MVP-Plan.md:L84](../../AI-Project-Documentation-MVP-Plan.md#L84) |
| D54 | FR-054 | Approvata | M3-08 (T033), M4-08 (T045), M6-01 (T055) | [AI-Project-Documentation-MVP-Plan.md:L85](../../AI-Project-Documentation-MVP-Plan.md#L85) |
| D55 | FR-055 | Approvata | M0-01 (T001), M0-02 (T002), M0-03 (T003), M0-04 (T004), M0-05 (T005), M0-07 (T007) | [AI-Project-Documentation-MVP-Plan.md:L86](../../AI-Project-Documentation-MVP-Plan.md#L86) |
| D56 | FR-056 | Approvata | M0-07 (T007), M6-05 (T059) | [AI-Project-Documentation-MVP-Plan.md:L87](../../AI-Project-Documentation-MVP-Plan.md#L87) |
| D57 | FR-057 | Approvata | M6-01 (T055), M6-02 (T056), M6-03 (T057), M6-04 (T058), M6-05 (T059) | [AI-Project-Documentation-MVP-Plan.md:L88](../../AI-Project-Documentation-MVP-Plan.md#L88) |
| A49 | FR-058 | Approvata dopo le 48 decisioni | M0-05 (T005), M1-09 (T016), M3-09 (T034), M3-10 (T035) | [AI-Project-Documentation-MVP-Plan.md:L121-L131](../../AI-Project-Documentation-MVP-Plan.md#L121) |
| A50 | FR-059 | Confermata dal committente | M3-02 (T027), M3-07 (T032), M3-10 (T035) | [AI-Project-Documentation-MVP-Plan.md:L136](../../AI-Project-Documentation-MVP-Plan.md#L136) |
| A51 | FR-060 | Confermata dal committente | M3-03 (T028), M3-11 (T036), M4-09 (T046) | [AI-Project-Documentation-MVP-Plan.md:L137](../../AI-Project-Documentation-MVP-Plan.md#L137) |
| A52 | FR-061 | Confermata dal committente | M3-03 (T028), M3-12 (T037), M4-09 (T046) | [AI-Project-Documentation-MVP-Plan.md:L138](../../AI-Project-Documentation-MVP-Plan.md#L138) |
| A53 | nessuno: proposta | ADR tecnico proposto, da formalizzare in M0 | M0-05 (T005), M2-09 (T025), M3-10 (T035) | [AI-Project-Documentation-MVP-Plan.md:L139](../../AI-Project-Documentation-MVP-Plan.md#L139) |
| A54 | nessuno: proposta | ADR tecnico proposto, da formalizzare in M0 | M0-05 (T005), M1-09 (T016), M3-09 (T034) | [AI-Project-Documentation-MVP-Plan.md:L140](../../AI-Project-Documentation-MVP-Plan.md#L140) |

## Matrice task -> decisioni / requisiti

| ID originale | Alias T | Decisioni (inferenza) | Alias FR | Dipendenze originali | Provenienza descrizione e AC |
|---|---|---|---|---|---|
| M0-01 | T001 | D05, D10, D25, D39, D55 | FR-005, FR-010, FR-025, FR-039, FR-055 | — | [AI-Project-Documentation-Tasks.md:L26](../../AI-Project-Documentation-Tasks.md#L26) |
| M0-02 | T002 | D04, D07, D08, D28, D55 | FR-004, FR-007, FR-008, FR-028, FR-055 | — | [AI-Project-Documentation-Tasks.md:L27](../../AI-Project-Documentation-Tasks.md#L27) |
| M0-03 | T003 | D20, D21, D22, D51, D55 | FR-020, FR-021, FR-022, FR-051, FR-055 | — | [AI-Project-Documentation-Tasks.md:L28](../../AI-Project-Documentation-Tasks.md#L28) |
| M0-04 | T004 | D28, D32, D36, D55 | FR-028, FR-032, FR-036, FR-055 | M0-01,M0-02 | [AI-Project-Documentation-Tasks.md:L29](../../AI-Project-Documentation-Tasks.md#L29) |
| M0-05 | T005 | D03, D09, D39, D40, D42, D48, D55, A49, A53, A54 | FR-003, FR-009, FR-039, FR-040, FR-042, FR-048, FR-055, FR-058 | — | [AI-Project-Documentation-Tasks.md:L30](../../AI-Project-Documentation-Tasks.md#L30) |
| M0-06 | T006 | D35, D43 | FR-035, FR-043 | M0-01 | [AI-Project-Documentation-Tasks.md:L31](../../AI-Project-Documentation-Tasks.md#L31) |
| M0-07 | T007 | D55, D56 | FR-055, FR-056 | M0-01..M0-06 | [AI-Project-Documentation-Tasks.md:L32](../../AI-Project-Documentation-Tasks.md#L32) |
| M1-01 | T008 | D51, D52 | FR-051, FR-052 | M0 | [AI-Project-Documentation-Tasks.md:L40](../../AI-Project-Documentation-Tasks.md#L40) |
| M1-02 | T009 | D10, D11, D44, D53 | FR-010, FR-011, FR-044, FR-053 | M1-01 | [AI-Project-Documentation-Tasks.md:L41](../../AI-Project-Documentation-Tasks.md#L41) |
| M1-03 | T010 | D17, D21 | FR-017, FR-021 | M1-01 | [AI-Project-Documentation-Tasks.md:L42](../../AI-Project-Documentation-Tasks.md#L42) |
| M1-04 | T011 | D03, D12, D48 | FR-003, FR-012, FR-048 | M1-03 | [AI-Project-Documentation-Tasks.md:L43](../../AI-Project-Documentation-Tasks.md#L43) |
| M1-05 | T012 | D03, D12, D30, D51 | FR-003, FR-012, FR-030, FR-051 | M1-03,M1-04 | [AI-Project-Documentation-Tasks.md:L44](../../AI-Project-Documentation-Tasks.md#L44) |
| M1-06 | T013 | D01, D04, D49 | FR-001, FR-004, FR-049 | M1-03,M1-04 | [AI-Project-Documentation-Tasks.md:L45](../../AI-Project-Documentation-Tasks.md#L45) |
| M1-07 | T014 | D17, D26, D44, D45 | FR-017, FR-026, FR-044, FR-045 | M1-03 | [AI-Project-Documentation-Tasks.md:L46](../../AI-Project-Documentation-Tasks.md#L46) |
| M1-08 | T015 | D41, D43, D52, D53 | FR-041, FR-043, FR-052, FR-053 | M1-01,M1-02 | [AI-Project-Documentation-Tasks.md:L47](../../AI-Project-Documentation-Tasks.md#L47) |
| M1-09 | T016 | D26, A49, A54 | FR-026, FR-058 | M1-03,M1-04 | [AI-Project-Documentation-Tasks.md:L48](../../AI-Project-Documentation-Tasks.md#L48) |
| M2-01 | T017 | D01, D02, D13, D14, D38 | FR-001, FR-002, FR-013, FR-014, FR-038 | M1 | [AI-Project-Documentation-Tasks.md:L56](../../AI-Project-Documentation-Tasks.md#L56) |
| M2-02 | T018 | D04, D07, D08, D17, D49 | FR-004, FR-007, FR-008, FR-017, FR-049 | M0-02,M1-07 | [AI-Project-Documentation-Tasks.md:L57](../../AI-Project-Documentation-Tasks.md#L57) |
| M2-03 | T019 | D07, D28, D36, D37 | FR-007, FR-028, FR-036, FR-037 | M0-01,M2-02 | [AI-Project-Documentation-Tasks.md:L58](../../AI-Project-Documentation-Tasks.md#L58) |
| M2-04 | T020 | D13, D37 | FR-013, FR-037 | M2-03 | [AI-Project-Documentation-Tasks.md:L59](../../AI-Project-Documentation-Tasks.md#L59) |
| M2-05 | T021 | D02, D30, D31, D32, D36 | FR-002, FR-030, FR-031, FR-032, FR-036 | M2-01,M2-03 | [AI-Project-Documentation-Tasks.md:L60](../../AI-Project-Documentation-Tasks.md#L60) |
| M2-06 | T022 | D28, D35, D36 | FR-028, FR-035, FR-036 | M2-05 | [AI-Project-Documentation-Tasks.md:L61](../../AI-Project-Documentation-Tasks.md#L61) |
| M2-07 | T023 | D01, D13, D14 | FR-001, FR-013, FR-014 | M2-01..M2-06 | [AI-Project-Documentation-Tasks.md:L62](../../AI-Project-Documentation-Tasks.md#L62) |
| M2-08 | T024 | D02, D14, D32, D43 | FR-002, FR-014, FR-032, FR-043 | M2-07 | [AI-Project-Documentation-Tasks.md:L63](../../AI-Project-Documentation-Tasks.md#L63) |
| M2-09 | T025 | D02, D32, D36, A53 | FR-002, FR-032, FR-036 | M2-06,M1-09 | [AI-Project-Documentation-Tasks.md:L64](../../AI-Project-Documentation-Tasks.md#L64) |
| M3-01 | T026 | D05, D25, D26, D34, D39 | FR-005, FR-025, FR-026, FR-034, FR-039 | M0-01,M1-07 | [AI-Project-Documentation-Tasks.md:L72](../../AI-Project-Documentation-Tasks.md#L72) |
| M3-02 | T027 | D18, D26, D45, A50 | FR-018, FR-026, FR-045, FR-059 | M2,M3-01,M1-09 | [AI-Project-Documentation-Tasks.md:L73](../../AI-Project-Documentation-Tasks.md#L73) |
| M3-03 | T028 | D28, D31, A51, A52 | FR-028, FR-031, FR-060, FR-061 | M3-02 | [AI-Project-Documentation-Tasks.md:L74](../../AI-Project-Documentation-Tasks.md#L74) |
| M3-04 | T029 | D19 | FR-019 | M3-03,M3-09,M3-11,M3-12 | [AI-Project-Documentation-Tasks.md:L75](../../AI-Project-Documentation-Tasks.md#L75) |
| M3-05 | T030 | D09, D15, D16 | FR-009, FR-015, FR-016 | M1,M3-01 | [AI-Project-Documentation-Tasks.md:L76](../../AI-Project-Documentation-Tasks.md#L76) |
| M3-06 | T031 | D09, D38 | FR-009, FR-038 | M3-05,M2-01 | [AI-Project-Documentation-Tasks.md:L77](../../AI-Project-Documentation-Tasks.md#L77) |
| M3-07 | T032 | D18, D19, D26, A50 | FR-018, FR-019, FR-026, FR-059 | M3-02..M3-06,M3-09..M3-12 | [AI-Project-Documentation-Tasks.md:L78](../../AI-Project-Documentation-Tasks.md#L78) |
| M3-08 | T033 | D43, D54 | FR-043, FR-054 | M3-07 | [AI-Project-Documentation-Tasks.md:L79](../../AI-Project-Documentation-Tasks.md#L79) |
| M3-09 | T034 | D18, D26, D45, A49, A54 | FR-018, FR-026, FR-045, FR-058 | M1-09,M3-01,M3-03 | [AI-Project-Documentation-Tasks.md:L80](../../AI-Project-Documentation-Tasks.md#L80) |
| M3-10 | T035 | D18, D26, A49, A50, A53 | FR-018, FR-026, FR-058, FR-059 | M3-09,M2-09 | [AI-Project-Documentation-Tasks.md:L81](../../AI-Project-Documentation-Tasks.md#L81) |
| M3-11 | T036 | A51 | FR-060 | M3-03,M3-10 | [AI-Project-Documentation-Tasks.md:L82](../../AI-Project-Documentation-Tasks.md#L82) |
| M3-12 | T037 | D31, A52 | FR-031, FR-061 | M3-11 | [AI-Project-Documentation-Tasks.md:L83](../../AI-Project-Documentation-Tasks.md#L83) |
| M4-01 | T038 | D21, D50 | FR-021, FR-050 | M0-03,M3 | [AI-Project-Documentation-Tasks.md:L91](../../AI-Project-Documentation-Tasks.md#L91) |
| M4-02 | T039 | D06, D21, D23, D24 | FR-006, FR-021, FR-023, FR-024 | M4-01 | [AI-Project-Documentation-Tasks.md:L92](../../AI-Project-Documentation-Tasks.md#L92) |
| M4-03 | T040 | D20, D47, D50, D51 | FR-020, FR-047, FR-050, FR-051 | M4-01 | [AI-Project-Documentation-Tasks.md:L93](../../AI-Project-Documentation-Tasks.md#L93) |
| M4-04 | T041 | D28, D33 | FR-028, FR-033 | M4-02 | [AI-Project-Documentation-Tasks.md:L94](../../AI-Project-Documentation-Tasks.md#L94) |
| M4-05 | T042 | D20, D27, D45, D47 | FR-020, FR-027, FR-045, FR-047 | M4-03,M4-04 | [AI-Project-Documentation-Tasks.md:L95](../../AI-Project-Documentation-Tasks.md#L95) |
| M4-06 | T043 | D18, D29 | FR-018, FR-029 | M4-05,M3 | [AI-Project-Documentation-Tasks.md:L96](../../AI-Project-Documentation-Tasks.md#L96) |
| M4-07 | T044 | D08, D21, D22, D28, D50 | FR-008, FR-021, FR-022, FR-028, FR-050 | M4-03 | [AI-Project-Documentation-Tasks.md:L97](../../AI-Project-Documentation-Tasks.md#L97) |
| M4-08 | T045 | D06, D22, D24, D27, D33, D43, D54 | FR-006, FR-022, FR-024, FR-027, FR-033, FR-043, FR-054 | M4-01..M4-07,M4-09 | [AI-Project-Documentation-Tasks.md:L98](../../AI-Project-Documentation-Tasks.md#L98) |
| M4-09 | T046 | D28, D31, A51, A52 | FR-028, FR-031, FR-060, FR-061 | M4-02,M3-11,M3-12 | [AI-Project-Documentation-Tasks.md:L99](../../AI-Project-Documentation-Tasks.md#L99) |
| M5-01 | T047 | D05, D25, D39 | FR-005, FR-025, FR-039 | M3-01 | [AI-Project-Documentation-Tasks.md:L107](../../AI-Project-Documentation-Tasks.md#L107) |
| M5-02 | T048 | D05, D34 | FR-005, FR-034 | M3-01,M4 | [AI-Project-Documentation-Tasks.md:L108](../../AI-Project-Documentation-Tasks.md#L108) |
| M5-03 | T049 | D44, D53 | FR-044, FR-053 | M1-07,M2,M3 | [AI-Project-Documentation-Tasks.md:L109](../../AI-Project-Documentation-Tasks.md#L109) |
| M5-04 | T050 | D42 | FR-042 | M1-02,M3 | [AI-Project-Documentation-Tasks.md:L110](../../AI-Project-Documentation-Tasks.md#L110) |
| M5-05 | T051 | D40 | FR-040 | M1-03,M1-06 | [AI-Project-Documentation-Tasks.md:L111](../../AI-Project-Documentation-Tasks.md#L111) |
| M5-06 | T052 | D40 | FR-040 | M5-05 | [AI-Project-Documentation-Tasks.md:L112](../../AI-Project-Documentation-Tasks.md#L112) |
| M5-07 | T053 | D46 | FR-046 | M2,M3 | [AI-Project-Documentation-Tasks.md:L113](../../AI-Project-Documentation-Tasks.md#L113) |
| M5-08 | T054 | D39, D41, D43 | FR-039, FR-041, FR-043 | M1-08,M4 | [AI-Project-Documentation-Tasks.md:L114](../../AI-Project-Documentation-Tasks.md#L114) |
| M6-01 | T055 | D40, D53, D54, D57 | FR-040, FR-053, FR-054, FR-057 | M5 | [AI-Project-Documentation-Tasks.md:L122](../../AI-Project-Documentation-Tasks.md#L122) |
| M6-02 | T056 | D35, D57 | FR-035, FR-057 | M6-01 | [AI-Project-Documentation-Tasks.md:L123](../../AI-Project-Documentation-Tasks.md#L123) |
| M6-03 | T057 | D42, D57 | FR-042, FR-057 | M6-02 | [AI-Project-Documentation-Tasks.md:L124](../../AI-Project-Documentation-Tasks.md#L124) |
| M6-04 | T058 | D43, D57 | FR-043, FR-057 | M6-03 | [AI-Project-Documentation-Tasks.md:L125](../../AI-Project-Documentation-Tasks.md#L125) |
| M6-05 | T059 | D56, D57 | FR-056, FR-057 | M6-04 | [AI-Project-Documentation-Tasks.md:L126](../../AI-Project-Documentation-Tasks.md#L126) |

## Matrice gate / SC <-> milestone e task

| Gate | Alias SC | Task nella milestone (ambito, non copertura provata) | Provenienza |
|---|---|---|---|
| M0 | SC-001 | M0-01, M0-02, M0-03, M0-04, M0-05, M0-06, M0-07 | [AI-Project-Documentation-Tasks.md:L34](../../AI-Project-Documentation-Tasks.md#L34) |
| M1 | SC-002 | M1-01, M1-02, M1-03, M1-04, M1-05, M1-06, M1-07, M1-08, M1-09 | [AI-Project-Documentation-Tasks.md:L50](../../AI-Project-Documentation-Tasks.md#L50) |
| M2 | SC-003 | M2-01, M2-02, M2-03, M2-04, M2-05, M2-06, M2-07, M2-08, M2-09 | [AI-Project-Documentation-Tasks.md:L66](../../AI-Project-Documentation-Tasks.md#L66) |
| M3 | SC-004 | M3-01, M3-02, M3-03, M3-04, M3-05, M3-06, M3-07, M3-08, M3-09, M3-10, M3-11, M3-12 | [AI-Project-Documentation-Tasks.md:L85](../../AI-Project-Documentation-Tasks.md#L85) |
| M4 | SC-005 | M4-01, M4-02, M4-03, M4-04, M4-05, M4-06, M4-07, M4-08, M4-09 | [AI-Project-Documentation-Tasks.md:L101](../../AI-Project-Documentation-Tasks.md#L101) |
| M5 | SC-006 | M5-01, M5-02, M5-03, M5-04, M5-05, M5-06, M5-07, M5-08 | [AI-Project-Documentation-Tasks.md:L116](../../AI-Project-Documentation-Tasks.md#L116) |
| M6 | SC-007 | M6-01, M6-02, M6-03, M6-04, M6-05 | [AI-Project-Documentation-Tasks.md:L128](../../AI-Project-Documentation-Tasks.md#L128) |

## Provenienza scenari / US

| Alias | Elemento originale | Provenienza |
|---|---|---|
| US1 | Passo 1 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L235](../../AI-Project-Documentation-MVP-Plan.md#L235) |
| US2 | Passo 2 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L236](../../AI-Project-Documentation-MVP-Plan.md#L236) |
| US3 | Passo 3 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L237](../../AI-Project-Documentation-MVP-Plan.md#L237) |
| US4 | Passo 4 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L238](../../AI-Project-Documentation-MVP-Plan.md#L238) |
| US5 | Passo 5 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L239](../../AI-Project-Documentation-MVP-Plan.md#L239) |
| US6 | Passo 6 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L240](../../AI-Project-Documentation-MVP-Plan.md#L240) |
| US7 | Passo 7 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L241](../../AI-Project-Documentation-MVP-Plan.md#L241) |
| US8 | Passo 8 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L242](../../AI-Project-Documentation-MVP-Plan.md#L242) |
| US9 | Passo 9 del workflow, non nuova user story | [AI-Project-Documentation-MVP-Plan.md:L243](../../AI-Project-Documentation-MVP-Plan.md#L243) |

## Indice di provenienza integrale per sezioni

Le copie preservano tutte le righe, anche Q1-Q48, elementi senza ID, DoD, must pass, stime e change log. Ogni intervallo termina prima della successiva intestazione di qualsiasi livello. La provenienza di ogni elemento non aliasato si ricava dall intervallo di sezione e dall ordine invariato delle righe.

| Fonte | Sezione originale | Righe | Copia |
|---|---|---|---|
| AI-Project-Documentation-MVP-Plan.md | # AI Project Documentation — Baseline approvata e piano di delivery | [AI-Project-Documentation-MVP-Plan.md:L1-L7](../../AI-Project-Documentation-MVP-Plan.md#L1) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 1. Risultato atteso e vincoli | [AI-Project-Documentation-MVP-Plan.md:L8-L15](../../AI-Project-Documentation-MVP-Plan.md#L8) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### Definizione dei termini | [AI-Project-Documentation-MVP-Plan.md:L16-L22](../../AI-Project-Documentation-MVP-Plan.md#L16) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 2. Registro consolidato delle decisioni | [AI-Project-Documentation-MVP-Plan.md:L23-L89](../../AI-Project-Documentation-MVP-Plan.md#L23) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### Mappatura fedele domande 1–48 | [AI-Project-Documentation-MVP-Plan.md:L90-L120](../../AI-Project-Documentation-MVP-Plan.md#L90) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## Decisione aggiuntiva A49 — Microsoft Agent Framework per le conversazioni (2026-10-14) | [AI-Project-Documentation-MVP-Plan.md:L121-L131](../../AI-Project-Documentation-MVP-Plan.md#L121) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## Decisioni aggiuntive A50–A54 — esperienza conversazionale e requisiti (2026-10-14) | [AI-Project-Documentation-MVP-Plan.md:L132-L143](../../AI-Project-Documentation-MVP-Plan.md#L132) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 3. Architettura logica target | [AI-Project-Documentation-MVP-Plan.md:L144-L173](../../AI-Project-Documentation-MVP-Plan.md#L144) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 4. Dati, storage e KB | [AI-Project-Documentation-MVP-Plan.md:L174-L175](../../AI-Project-Documentation-MVP-Plan.md#L174) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 4.1 Entità minime (da affinare in migration design) | [AI-Project-Documentation-MVP-Plan.md:L176-L181](../../AI-Project-Documentation-MVP-Plan.md#L176) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 4.2 Flusso ingestion | [AI-Project-Documentation-MVP-Plan.md:L182-L192](../../AI-Project-Documentation-MVP-Plan.md#L182) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 4.3 Retrieval e sicurezza fonti | [AI-Project-Documentation-MVP-Plan.md:L193-L198](../../AI-Project-Documentation-MVP-Plan.md#L193) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 4.4 Ricerca web / importazione nella KB | [AI-Project-Documentation-MVP-Plan.md:L199-L204](../../AI-Project-Documentation-MVP-Plan.md#L199) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 5. Routing, prompt e scheduler AI | [AI-Project-Documentation-MVP-Plan.md:L205-L206](../../AI-Project-Documentation-MVP-Plan.md#L205) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 5.1 Provider e modello per fase | [AI-Project-Documentation-MVP-Plan.md:L207-L212](../../AI-Project-Documentation-MVP-Plan.md#L207) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 5.2 Conversazioni, budget e compattazione MAF | [AI-Project-Documentation-MVP-Plan.md:L213-L218](../../AI-Project-Documentation-MVP-Plan.md#L213) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 5.3 Comportamento chat-first e retrieval selettivo | [AI-Project-Documentation-MVP-Plan.md:L219-L228](../../AI-Project-Documentation-MVP-Plan.md#L219) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 5.4 Scheduler e checkpoint | [AI-Project-Documentation-MVP-Plan.md:L229-L232](../../AI-Project-Documentation-MVP-Plan.md#L229) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 6. Workflow e UX | [AI-Project-Documentation-MVP-Plan.md:L233-L246](../../AI-Project-Documentation-MVP-Plan.md#L233) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 7. Export e revisioni | [AI-Project-Documentation-MVP-Plan.md:L247-L250](../../AI-Project-Documentation-MVP-Plan.md#L247) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 8. Security, deployment, osservabilità e backup | [AI-Project-Documentation-MVP-Plan.md:L251-L259](../../AI-Project-Documentation-MVP-Plan.md#L251) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 9. Testing e quality gates | [AI-Project-Documentation-MVP-Plan.md:L260-L267](../../AI-Project-Documentation-MVP-Plan.md#L260) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 10. Strategia di rilascio e stima | [AI-Project-Documentation-MVP-Plan.md:L268-L269](../../AI-Project-Documentation-MVP-Plan.md#L268) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 10.1 Sequenza milestone (gate obbligatori) | [AI-Project-Documentation-MVP-Plan.md:L270-L281](../../AI-Project-Documentation-MVP-Plan.md#L270) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ### 10.2 Stima revisionata (ore uomo singolo sviluppatore esperto, AI coding assistita) | [AI-Project-Documentation-MVP-Plan.md:L282-L298](../../AI-Project-Documentation-MVP-Plan.md#L282) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 11. Istruzioni vincolanti per il coding agent | [AI-Project-Documentation-MVP-Plan.md:L299-L311](../../AI-Project-Documentation-MVP-Plan.md#L299) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## 12. Rischi e punti di verifica tecnica (non nuove domande al committente) | [AI-Project-Documentation-MVP-Plan.md:L312-L332](../../AI-Project-Documentation-MVP-Plan.md#L312) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## Change log 2.1 — integrazione conversazionale MAF | [AI-Project-Documentation-MVP-Plan.md:L333-L339](../../AI-Project-Documentation-MVP-Plan.md#L333) | plan.md |
| AI-Project-Documentation-MVP-Plan.md | ## Change log 2.2 — A50–A54 e riallocazione backlog (2026-10-14) | [AI-Project-Documentation-MVP-Plan.md:L340-L345](../../AI-Project-Documentation-MVP-Plan.md#L340) | plan.md |
| AI-Project-Documentation-Tasks.md | # AI Project Documentation — Backlog di implementazione | [AI-Project-Documentation-Tasks.md:L1-L4](../../AI-Project-Documentation-Tasks.md#L1) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Regole di lettura | [AI-Project-Documentation-Tasks.md:L5-L13](../../AI-Project-Documentation-Tasks.md#L5) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Decisione A49 — MAF conversazionale | [AI-Project-Documentation-Tasks.md:L14-L17](../../AI-Project-Documentation-Tasks.md#L14) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Decisioni A50–A54 — Chat, conferme e conflitti | [AI-Project-Documentation-Tasks.md:L18-L21](../../AI-Project-Documentation-Tasks.md#L18) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M0 — Proof tecnologico, contratti e ADR (38 h; range 30–45) | [AI-Project-Documentation-Tasks.md:L22-L35](../../AI-Project-Documentation-Tasks.md#L22) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M1 — Foundation, autenticazione, deployment iniziale (78 h; 60–95) | [AI-Project-Documentation-Tasks.md:L36-L51](../../AI-Project-Documentation-Tasks.md#L36) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M2 — Knowledge, Docling, RAG avanzato (106 h; 80–135) | [AI-Project-Documentation-Tasks.md:L52-L67](../../AI-Project-Documentation-Tasks.md#L52) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M3 — Flusso PM, domande e ricerca web (121 h; 95–150) | [AI-Project-Documentation-Tasks.md:L68-L86](../../AI-Project-Documentation-Tasks.md#L68) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M4 — Documento visuale, revisione, export (134 h; 105–165) | [AI-Project-Documentation-Tasks.md:L87-L102](../../AI-Project-Documentation-Tasks.md#L87) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M5 — Hardening, admin e operations (120 h; 90–150) | [AI-Project-Documentation-Tasks.md:L103-L117](../../AI-Project-Documentation-Tasks.md#L103) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## M6 — Pilota reale e accettazione (50 h dev; range 35–65) | [AI-Project-Documentation-Tasks.md:L118-L129](../../AI-Project-Documentation-Tasks.md#L118) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Trasversalità e criteri aggiuntivi | [AI-Project-Documentation-Tasks.md:L130-L131](../../AI-Project-Documentation-Tasks.md#L130) | tasks.md |
| AI-Project-Documentation-Tasks.md | ### Sicurezza (must pass) | [AI-Project-Documentation-Tasks.md:L132-L139](../../AI-Project-Documentation-Tasks.md#L132) | tasks.md |
| AI-Project-Documentation-Tasks.md | ### Meccanismi LLM e KB (must pass) | [AI-Project-Documentation-Tasks.md:L140-L147](../../AI-Project-Documentation-Tasks.md#L140) | tasks.md |
| AI-Project-Documentation-Tasks.md | ### Rendering/editor (must pass) | [AI-Project-Documentation-Tasks.md:L148-L153](../../AI-Project-Documentation-Tasks.md#L148) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Stima e critical path | [AI-Project-Documentation-Tasks.md:L154-L168](../../AI-Project-Documentation-Tasks.md#L154) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Prompt iniziale per coding agent (non avviare task fuori milestone) | [AI-Project-Documentation-Tasks.md:L169-L173](../../AI-Project-Documentation-Tasks.md#L169) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Change log 1.1 (2026-10-14) | [AI-Project-Documentation-Tasks.md:L174-L177](../../AI-Project-Documentation-Tasks.md#L174) | tasks.md |
| AI-Project-Documentation-Tasks.md | ## Change log 1.2 (2026-10-14) | [AI-Project-Documentation-Tasks.md:L178-L182](../../AI-Project-Documentation-Tasks.md#L178) | tasks.md |

## Informazioni mancanti, ambiguita e contraddizioni preservate

AUD identifica annotazioni di audit, non nuove decisioni o correzioni. La natura parziale delle relazioni sotto si applica anche alle matrici.

| ID | Osservazione | Provenienza | Trattamento |
|---|---|---|---|
| AUD-01 | A53/A54 proposte, ma varie sezioni ne descrivono gia il comportamento e il preambolo parla globalmente di decisioni aggiuntive | Piano L6, L139-L140, L219-L227; backlog L20/L30 | Entrambi conservati, nessuna promozione ad approvato |
| AUD-02 | Percorso candidato M3-04 -> M4-09 non espresso nella riga M4-09; M0-02 -> M1-07 passa dal gate, non da arco tecnico diretto | Backlog L46/L99/L167 | Dipendenze e percorso invariati |
| AUD-03 | M3-02/03 precedono M3-09 che compone il contesto; bootstrap non chiarito | Backlog L73-L80 | Nessuna dipendenza aggiunta |
| AUD-04 | A52 vieta prevalenza automatica per tipo fonte; must pass attribuisce precedenza a risposte PM su inferenze/estratti non verificati | Piano L138; backlog L146 | Confine ipotesi/conflitti da verificare |
| AUD-05 | D35: runner CLI non assegnato inequivocabilmente ai task dataset/benchmark | Piano L61; backlog M0-06/M2-06 | Copertura parziale |
| AUD-06 | D43: scheduling/runner eval AI reali non assegnati inequivocabilmente | Piano L69/L260-L264 | Copertura parziale |
| AUD-07 | D49: quote storage progetto/KB e UI Admin non coperte integralmente da upload limitato | Piano L80; backlog M1-06 | Copertura parziale; M5-03 riguarda scheduler, non storage |
| AUD-08 | D30/D31: test compatibilita versioni e storico cliente poco espliciti | Piano L56-L57/L193-L197; backlog M1-05/M2-05 | Copertura parziale |
| AUD-09 | Gate M0 richiede prova integrata analisi locale e RAG citazioni, assegnazione completa non inequivocabile | Backlog L26-L34 | Gate conservato |
| AUD-10 | Soglie performance/RAG/LLM, budget, criticita/ambiguita, RPO/RTO e retention non completi | Piano sezioni 5/8/9; backlog M3-09/M5-03/M5-05/06 | Nessun valore inventato |
| AUD-11 | Task ampi e aggregati; versioni, percorsi e contratti definitivi mancanti | Backlog M0-05/M4-03/M5-04/06; piano sezioni 3/4/11 | Nessuna decomposizione implementativa |
| AUD-12 | Quote/priorita, osservabilita completa e backup in M5 dopo pilota funzionale M4; minimi M4 da precisare | Backlog gate M4/M5 e M5-03/04/05 | Nessun anticipo automatico |
| AUD-13 | GLOBAL e discipline approvazione/indicizzazione piu dettagliati nel piano che negli AC | Piano sezione 4.2/4.3; backlog M2-01/08 | Test mancanti non aggiunti |
| AUD-14 | Rischi runtime/JSON, ambiente Docling, converter/licenze, idempotenza, reindex, SSRF/egress, restore richiedono prove | Piano sezione 12; backlog M0/M2/M5 | Nessun esito tecnico assunto |
| AUD-15 | User story prioritarie, test indipendenti e percorsi richiesti dai template non sono nelle fonti | Template Spec Kit locali e fonti | Alias US, nessuna priorita/percorso inventata |

## Compatibilita e limiti Spec Kit 1.2.0

Nomi dei file, sezioni principali e alias FR/SC/T consentono lettura e analisi documentale; non si dichiara conformita completa al workflow. tasks.md non e prodotto da speckit-tasks, constitution ancora template, contratti/schema/readiness non risolti. Conservate milestone invece del raggruppamento primario per user story. FR aliasa record anche tecnici/governance non atomici; SC aliasa gate anche tecnici/outcome umani. Copie integrali sono evidenza, da non ricontare come nuove definizioni.

Nessun workflow, hook o selezione persistente feature eseguito; feature.json non modificato. Per un futuro controllo prerequisiti impostare SPECIFY_FEATURE_DIRECTORY=specs/001-documentation-audit e SPECIFY_FEATURE_NO_PERSIST=1. Non assumere read-only il checker senza protezione. Questa nota non esegue il controllo o speckit-analyze.

## Validazione della proiezione

- PASS: 59/59 task originali, zero omissioni o aggiunte; T001-T059 unici e biunivoci.
- PASS: descrizioni, dipendenze testuali, stime e AC dei 59 task confrontati esattamente con le celle sorgente.
- PASS: 57 record D e 6 record A; 61 FR unici per D01-D57/A49-A52; nessun FR per A53/A54.
- PASS: stati e testi decisionali originali preservati; A53/A54 restano proposte.
- PASS: 7 gate originali/alias SC unici, 9 azioni workflow/alias US con provenienza.
- PASS: matrici nei due sensi coerenti dalla stessa relazione molti-a-molti; nessun riferimento task sconosciuto.
- PASS: copie integrali preservano tutte le righe sorgente; normalizzati soltanto terminatori di riga nella copia.
- PASS: milestone M0-M6; distribuzione task 7/9/9/12/9/8/5; totale 647 h invariato.
- PASS: hash byte SHA-256 originali invariati prima/dopo scrittura; nessun file esterno alla directory autorizzata scritto.
- LIMITAZIONE: fedelta e integrita documentale, non validita tecnica, assenza di contraddizioni, conformita costituzionale o readiness implementativa.
