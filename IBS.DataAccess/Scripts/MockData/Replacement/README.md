# GL replacement test data

Upload the CSV files directly in GL Import. Each file has the current thirteen columns. Use files 01 through 05 in order, previewing and confirming each once. File 06 is optional. Files 07 through 09 must be rejected.

These fictional transactions use Company `GL Replacement Test A` and `GL Replacement Test B`, with references `RT261024-001` through `RT261024-005` and one deliberate lowercase reference. They do not overlap the earlier counterparty samples. The accounts, exact account names, and active customer/supplier IDs were checked against the configured development database on October 24, 2026. All six valid previews passed, and all three invalid previews produced the intended errors. No test transactions were imported into that database automatically.

Keep October and November 2026 open for the normal sequence. If using another database, download its template and check the account names and active IDs first. These files use the current uppercase account names; older mock files may contain outdated capitalization.

## Main sequence

The totals below are the **current GL rows for this test set after confirmation**, not the cumulative batch-history totals or the preview totals of the current file. Debit and credit must equal the amount shown.

| Step | File | What to check | Current GL lines | Transactions | Debit and credit each |
| --- | --- | --- | ---: | ---: | ---: |
| 1 | `01-initial.csv` | Creates opening capital 50000, sale 12000, purchase 2500, and expense 1000.4321. | 8 | 4 | 65500.4321 |
| 2 | `02-replace-amounts-and-add-line.csv` | Replaces the sale with 15000, October 12, customer 2. Replaces the purchase with three lines totaling 3000, October 13, supplier 2, including a new 500 freight line. | 9 | 4 | 69000.4321 |
| 3 | `03-replace-remove-lines-and-clear-optional.csv` | Replaces the three purchase lines with two cash-purchase lines totaling 900. Old freight and payable lines disappear. Sub-account and counterparty fields are blank on both new lines. | 8 | 4 | 66900.4321 |
| 4 | `04-mixed-replacement-and-new.csv` | Replaces the sale with 18000, October 20, customer 3, and adds a 6000 collection. Opening, purchase, and expense remain unchanged. | 10 | 5 | 75900.4321 |
| 5 | `05-company-module-and-case-separation.csv` | Adds four separate transactions with similar references: company B, module Journal, lowercase module sales, and lowercase reference. The original company A / Sales / RT261024-002 remains 18000. | 18 | 9 | 79300.4321 |

After step 5, the original `GL Replacement Test A` transactions should be:

| Reference | ModuleType | Date | Lines | Debit and credit each | Optional fields |
| --- | --- | --- | ---: | ---: | --- |
| RT261024-001 | Journal | 2026-10-01 | 2 | 50000.0000 | Blank |
| RT261024-002 | Sales | 2026-10-20 | 2 | 18000.0000 | Counterparty Customer 3 on both lines; Customer 3 sub-account on receivable |
| RT261024-003 | Purchase | 2026-10-13 | 2 | 900.0000 | Blank |
| RT261024-004 | Disbursement | 2026-10-04 | 2 | 1000.4321 | Counterparty Supplier 2 on both lines |
| RT261024-005 | Collection | 2026-10-21 | 2 | 6000.0000 | Counterparty Customer 3 on both lines; Customer 3 sub-account on receivable |

The four additional step-5 transactions have totals 700, 800, 900, and 1000 respectively. There should be no original 12000 or intermediate 15000 sale, old 2500 or 3000 purchase, purchase payable, or purchase freight line remaining for their original keys. Import history still describes each original upload, so it is not the source for current GL totals.

## Rejection checks

Run these after step 5. Current GL lines, values, batch history, and audit records must remain unchanged when an upload is rejected.

| Upload | Expected result |
| --- | --- |
| `01-initial.csv` again | Rejected: `This file has already been imported.` It must not restore the old sale or purchase. Renaming an unchanged file also does not bypass its hash. |
| `07-invalid-unbalanced-mixed.csv` | Rejected because RT261024-090 has debit 100 and credit 99. The otherwise valid 20000 replacement sale in the same file must not be saved. |
| `08-invalid-multiple-dates.csv` | Rejected because one Company/ModuleType/Reference transaction has two dates, even though its debit and credit balance. |
| `09-invalid-cross-company-offset.csv` | Rejected with two unbalanced-transaction errors. Overall debit and credit both equal 123, but one company cannot offset the other. |

## Optional closed-period and date-change test

Use `06-optional-move-purchase-to-november.csv` after step 5. It keeps Company, ModuleType, and Reference unchanged but changes the purchase date from October 13 to November 5 and the amount from 900 to 950.

To test the closed-period safeguard, leave this file unimported until after closing October 2026 through the normal workflow. Follow the application's lock/close sequence, including any preceding periods it requires. Preview file 06: it must be rejected with the reopen-period message because the existing transaction is in October, even though the new date is November. Reopen October and any later closed months, then preview and confirm file 06. The October purchase must disappear and exactly two November purchase lines totaling 950 must remain.

If both periods are open, file 06 can be imported directly to test date replacement. After it succeeds, expect 18 current lines, 9 transactions, and debit/credit totals of **79350.4321** for the complete test set.

## Inspecting the results

Run `verify.sql` against the same database used by the app. It contains only SELECT queries and returns the summary, transactions, and current lines for these two test companies and test references. Reports that use closed snapshots require the normal chronological month closing before reconciliation.

The test set is intended for one ordered run: unchanged files cannot be imported twice. For another run, change the reference prefix consistently in every CSV, retaining the deliberate lowercase variant. Do not clear unrelated application data.
