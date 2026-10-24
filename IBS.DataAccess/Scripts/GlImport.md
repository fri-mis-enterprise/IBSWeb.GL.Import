# GL import

This workflow imports GL entries against existing chart-of-accounts, supplier, and customer records. Imports can add journals or replace previously imported transactions in a populated GL. Operational balances are unchanged.

Sign in and open **GL Import** from the main menu, beside **Request Form**. Any signed-in user can upload directly. Download the Excel template, fill **GL Upload**, save that sheet as **CSV UTF-8**, preview the CSV, correct any errors, then confirm the complete batch.

## Template and reference sheets

The single template download is `gl-template.xlsx`, containing:

- **GL Upload**: thirteen import headers, including optional Supplier/Customer fields, required free-text Company and ModuleType fields, and optional CounterpartyType/CounterpartyId fields, with date, amount, and text-code formats.
- **Chart of Accounts**: exact AccountNo and AccountName for visible accounts without children.
- **Customers**: SubAccountType, SubAccountId, CustomerCode, and CustomerName for active registered customers.
- **Suppliers**: SubAccountType, SubAccountId, SupplierCode, and SupplierName for active registered suppliers.
- **Instructions**: how to fill the upload sheet and export it as CSV.

Use SubAccountId from the reference sheet, not the customer/supplier code. The lists come from the current database each time the template is downloaded. They guide data entry; they do not create or update master records. Download again after master data changes. Preview and confirmation still validate the current database records.

Keep the Excel workbook for reference. Select **GL Upload** before saving as CSV UTF-8 and export the current sheet only. CSV holds a single sheet; the upload continues to accept `.csv` files, not the reference workbook.

## File format

```csv
Date,Reference,Description,AccountNo,AccountName,Debit,Credit,SubAccountType,SubAccountId,Company,CounterpartyType,CounterpartyId,ModuleType
2024-01-15,LEG-2024-0001,Illustrative legacy journal,SAMPLE-CASH,Legacy cash,1000.0000,0.0000,,,Example Company,,,Journal
2024-01-15,LEG-2024-0001,Illustrative legacy journal,SAMPLE-REVENUE,Legacy revenue,0.0000,1000.0000,,,Example Company,,,Journal
```

Replace the sample codes with existing visible posting accounts without children. Save as UTF-8 CSV; a UTF-8 BOM is supported. Preserve leading zeros in account codes. The headers and column order must match exactly.

The GL Upload sheet has thirteen columns, ending in ModuleType. One file can contain lines with and without sub-accounts or counterparties; leave each unused type/ID pair blank. Keep all thirteen fields on every CSV row exported from the template. Existing eight-column and ten-column files ending in Company and ten-column/twelve-column files with CounterpartyType/CounterpartyId remain supported. Each of these formats may append ModuleType as its last column. Headers distinguish formats of the same length. Files without Company must add it and supply its value on every line before uploading. Files containing a CounterpartyName input column are rejected; enter the type and ID instead.

Company is required free text on every GL line, up to 50 characters, trimmed before storage. No Company master record is required or created. A missing Company column or a blank/whitespace value blocks the entire upload during preview and confirmation. Existing GL entries remain unchanged by the migration, so the database column stays nullable for historical records. Use consistent names and capitalization: Company is part of the transaction replacement key.

ModuleType is free text, up to 50 characters, trimmed before storage in the existing GL ModuleType field. Use consistent spelling and capitalization for the same module across all rows and uploads, for example `Sales`, `Purchase`, `Disbursement`, `DebitMemo`, `CreditMemo`, `Collection`, or `Journal`. Other module names are allowed; this is not a fixed list. When the column is present, blank/whitespace values, values over 50 characters, and NUL characters block the entire upload during preview and confirmation. Older files without ModuleType retain the `LegacyGL` default. Existing entries are unchanged, and no schema migration is required.

For a counterparty, enter `CounterpartyType` (`Customer` or `Supplier`) and a positive integer `CounterpartyId` from an active registered record. Copy the type and database ID from the Customers or Suppliers reference sheet; the same type/ID values work for sub-accounts and counterparties. Supply both fields or leave both blank. Numeric enum values, other types, incomplete pairs, and missing/inactive records are rejected. IDs are qualified by type: customer ID 123 and supplier ID 123 are different records.

Preview resolves CounterpartyName from the selected master record. Confirmation validates the current record again and saves CounterpartyType, CounterpartyId, and the current CounterpartyName together in the existing GL columns. Empty, overlong (more than 200 characters), or NUL-containing master names block the entire batch. Users do not enter the name. No master records are created and no schema change is needed.

Counterparty and sub-account are independent; neither supplies the other's type, ID, or name. Match the source transaction's counterparty: sales use the customer and purchases use the supplier, even when a GL line has a different sub-account. The importer does not infer counterparty from ModuleType, the posting account, or sub-account.

Each row is one GL line. A transaction is identified by Company, ModuleType, and Reference, compared case-sensitively after trimming surrounding whitespace. All its lines must have one date and balance exactly. Transactions cannot offset another company's or module's imbalance. Use `yyyy-MM-dd` dates and a nonzero amount (positive or negative) on exactly one side, with zero on the other. Negative amounts keep their sign and stay in the supplied Debit or Credit column. Each transaction must still have equal signed debit and credit totals. Amounts must fit `numeric(18,4)`, with no currency symbols or thousands separators. Do not include totals or subtotal rows.

Reference is limited to 20 characters, AccountNo to 50, and AccountName to 200. The first seven fields and Company are required. AccountName must exactly match the chart-of-accounts name for AccountNo, including capitalization; leading and trailing CSV whitespace is trimmed before comparison. This check runs during preview and confirmation. AccountNo resolves the internal account ID, and the chart of accounts supplies financial classifications. No accounts, invoices, vouchers, operational balances, sub-account records, or inventory transactions are created.

### Supplier and customer sub-accounts

Fill the optional sub-account fields on the applicable lines in the same template:

```csv
Date,Reference,Description,AccountNo,AccountName,Debit,Credit,SubAccountType,SubAccountId,Company,CounterpartyType,CounterpartyId,ModuleType
2024-01-15,LEG-2024-0002,Illustrative supplier journal,SAMPLE-EXPENSE,Legacy expense,1000.0000,0.0000,,,Example Company,Supplier,123,Purchase
2024-01-15,LEG-2024-0002,Illustrative supplier journal,SAMPLE-PAYABLE,Legacy payable,0.0000,1000.0000,Supplier,123,Example Company,Supplier,123,Purchase
```

Replace the account codes, names, and ID with actual registered records. `SubAccountType` must be exactly `Supplier` or `Customer`; `SubAccountId` must be a positive integer for an active record of that type. Supply both fields or leave both blank. The same numeric ID may identify different supplier and customer records, so type is required. Bank/company types and numeric enum values are not accepted by this template.

Use the existing supplier/customer database ID as `SubAccountId`. GL import is independent of the format or process used to create those master records.

Preview resolves and displays registered names. Confirmation checks activity/existence again and stores the current name with the type and ID in the GL. The importer does not create master records. CounterpartyName is resolved independently from CounterpartyType and CounterpartyId. Rows without the sub-account columns or with blank pairs have no sub-account.

A file may contain at most 50,000 GL lines and 10 MB. Split larger files at journal boundaries. Preview shows up to 100 valid lines and 100 errors; any error blocks the entire batch. The original file stays in the uploading user's server-side preview cache for up to 15 minutes. Restarting the server or evicting the cache requires another upload.

## Posting and history

Confirmation revalidates the original file and current database state. To replace an imported transaction, upload its complete corrected set of lines using the same Company, ModuleType, and Reference. Date, accounts, amounts, descriptions, sub-accounts, and counterparties may change. All previous matching GL lines are permanently deleted and the new lines replace them, including removing lines omitted from the new upload. Transactions absent from the file are unchanged. A changed Company or ModuleType identifies a different transaction and leaves the old one in place. Older formats without ModuleType match only LegacyGL transactions.

Identical files remain rejected, including unchanged older files whose transactions have since been replaced. Non-imported GL entries cannot be replaced. An existing matching transaction spanning multiple dates or import batches is ambiguous and blocks the entire upload. No additional replacement prompt is shown; the normal preview and confirmation apply.

Concurrent imports and monthly close/reopen operations share a transaction advisory lock. Deletion, replacement lines, new batch metadata, and the audit entry commit in one transaction; any error rolls back the whole upload. The file hash retains its unique database index.

History retains the batch ID, filename, hash, importing user and time, date range, line and journal counts, and debit/credit totals. These describe the original upload, even after some transactions are replaced. Current GL lines link to the batch that supplied their latest values. Previous GL values are not archived or recoverable through this workflow. The original file is not retained after confirmation or cache expiry. No separate batch deletion action is provided.

## Financial reports

Supply the complete agreed coverage, including opening entries when required. Do not combine cumulative balances with the underlying movements for the same period.

Closed balances at or after the earliest affected date block the file. This includes both incoming dates and the dates of previous lines being replaced, so moving a transaction forward cannot bypass a closed period. Reopen affected and later closed months before importing. After importing all entries, lock and close months in chronological order. The existing closure sequence requires preceding months in each fiscal year to be closed, including empty months before the first imported month.

The existing trial balance uses stored period balances, and some financial reports use stored monthly earnings. Importing alone does not generate these snapshots. Per-company P&L and other financial reports require a later update to their filtering and calculations. Reconcile GL totals, trial balance, profit and loss, and balance sheet against the source reports. Existing maintenance/rebuild operations are intended for operational documents; imported journals have no corresponding operational documents.

Apply the migration to the intended database. If a database already has the earlier discarded implementation's migration, reconcile its migration history and schema before applying a newly generated migration with the same purpose.

## GL import verification

- Preview and import eight-column and ten-column files and the ten-column/twelve-column CounterpartyType/CounterpartyId variants, including blank sub-account pairs and supplier/customer records sharing the same numeric ID.
- Verify every supported format with ModuleType appended: trimmed names, custom names, case preservation, and correct sub-account/counterparty values. Reject blank/whitespace names, values over 50 characters, NUL characters, misplaced/duplicate ModuleType headers, and incorrect field counts. Confirm the preview and saved GL retain the supplied module name, while older files without ModuleType use LegacyGL.
- Require Company in each supported format and verify preview and saved GL entries contain the trimmed free-text name without a Company master record. Reject files without Company (the old seven-column/nine-column layouts), blank/whitespace company values, values over 50 characters, and NUL characters. Verify quoted names containing commas and a file where only one line is missing its company.
- Verify optional counterparty pair parsing and all three persisted fields: omitted columns, blank pairs, Customer/Supplier records sharing an ID, names different from the sub-account, and current master names. Reject incomplete pairs, numeric/unknown types, nonpositive/invalid IDs, missing/inactive records, and invalid master names. Change the master name/activity between preview and confirmation and verify confirmation resolves the current name or rejects the entire batch.
- Missing/inactive IDs, wrong types, incomplete pairs, account-name mismatches, unbalanced transactions, multiple dates within one transaction, closed periods, identical files, and ambiguous/non-imported matches must block the whole batch. Recheck changes to master records between preview and confirmation.
- Verify new-only, replacement-only, and mixed uploads; changed dates, accounts, amounts, descriptions, optional fields, and increased/reduced line counts. Old matching lines must disappear, only current values must affect GL totals, and unrelated transactions must remain unchanged. Verify company/module/case separation and trimmed keys.
- Verify repeated replacements, immutable original batch metadata, identical-file rejection after replacement, and full rollback after a database failure. Confirm no previous-value archive is created. Run concurrent imports and close/reopen operations to verify serialization and revalidation of both old and new dates.
- Verify the Excel template as a signed-in user: all thirteen GL Upload headers, ModuleType consistency instructions, reference IDs/codes/names, inactive-master exclusion, hidden/parent-account exclusion, empty reference lists, and leading-zero codes. Confirm anonymous users cannot access the template/upload actions.
- Export a completed GL Upload sheet as CSV UTF-8 and preview it, including mixed lines with/without sub-accounts. Reference sheets must not enter the CSV.
- Reconcile imported GL totals and supplier/customer subsidiary movements. After closing months chronologically, verify financial reports and following-month subsidiary openings, including PDF and Excel outputs.
