-- Compare this summary with the expected current totals in README.md.
SELECT
    COUNT(*) AS current_gl_lines,
    COUNT(DISTINCT (company, module_type, reference)) AS current_transactions,
    COALESCE(SUM(debit), 0) AS total_debit,
    COALESCE(SUM(credit), 0) AS total_credit
FROM filpride_general_ledger_books
WHERE company IN ('GL Replacement Test A', 'GL Replacement Test B')
  AND (reference LIKE 'RT261024-%' OR reference LIKE 'rt261024-%');

SELECT
    company,
    module_type,
    reference,
    MIN(date) AS first_date,
    MAX(date) AS last_date,
    COUNT(*) AS current_lines,
    SUM(debit) AS total_debit,
    SUM(credit) AS total_credit
FROM filpride_general_ledger_books
WHERE company IN ('GL Replacement Test A', 'GL Replacement Test B')
  AND (reference LIKE 'RT261024-%' OR reference LIKE 'rt261024-%')
GROUP BY company, module_type, reference
ORDER BY company, module_type, reference;

SELECT
    general_ledger_book_id,
    company,
    module_type,
    reference,
    date,
    account_no,
    account_title,
    description,
    debit,
    credit,
    sub_account_type,
    sub_account_id,
    sub_account_name,
    counterparty_type,
    counterparty_id,
    counterparty_name,
    import_batch_id
FROM filpride_general_ledger_books
WHERE company IN ('GL Replacement Test A', 'GL Replacement Test B')
  AND (reference LIKE 'RT261024-%' OR reference LIKE 'rt261024-%')
ORDER BY company, module_type, reference, general_ledger_book_id;
