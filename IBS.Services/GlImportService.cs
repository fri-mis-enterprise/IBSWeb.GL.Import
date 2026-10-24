using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using IBS.DataAccess.Data;
using IBS.DTOs;
using IBS.Models.Enums;
using IBS.Models.Filpride.Books;
using IBS.Models.Filpride.MasterFile;
using IBS.Utility.Helpers;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace IBS.Services
{
    public class GlImportService
    {
        public const string CsvHeader = "Date,Reference,Description,AccountNo,AccountName,Debit,Credit";
        public const string SubAccountCsvHeader = CsvHeader + ",SubAccountType,SubAccountId";
        public const string CompanyCsvHeader = SubAccountCsvHeader + ",Company";
        public const string CounterpartyCsvHeader = CompanyCsvHeader + ",CounterpartyType,CounterpartyId";
        public const string ModuleTypeCsvHeader = CounterpartyCsvHeader + ",ModuleType";
        public const int MaximumFileBytes = 10 * 1024 * 1024;
        public const int MaximumRows = 50000;
        public const int MaximumErrors = 100;
        internal const string PeriodLockSql = "SELECT pg_advisory_xact_lock(742036001)";
        private readonly ApplicationDbContext _dbContext;
        private readonly ISubAccountResolver _subAccountResolver;

        public GlImportService(ApplicationDbContext dbContext, ISubAccountResolver subAccountResolver)
        {
            _dbContext = dbContext;
            _subAccountResolver = subAccountResolver;
        }

        public async Task<byte[]> CreateTemplateAsync(CancellationToken cancellationToken = default)
        {
            var accounts = await _dbContext.FilprideChartOfAccounts.AsNoTracking()
                .Include(account => account.ParentAccount)
                .Where(account => !account.HasChildren)
                .OrderBy(account => account.AccountNumber)
                .Select(account => new
                {
                    account.AccountNumber,
                    account.AccountName,
                    ParentAccountNo = account.ParentAccount != null ? account.ParentAccount.AccountNumber : string.Empty,
                    ParentAccountName = account.ParentAccount != null ? account.ParentAccount.AccountName : string.Empty
                })
                .ToListAsync(cancellationToken);
            var customers = await _dbContext.FilprideCustomers.AsNoTracking()
                .Where(customer => customer.IsActive)
                .OrderBy(customer => customer.CustomerName).ThenBy(customer => customer.CustomerId)
                .Select(customer => new { customer.CustomerId, customer.CustomerCode, customer.CustomerName })
                .ToListAsync(cancellationToken);
            var suppliers = await _dbContext.FilprideSuppliers.AsNoTracking()
                .Where(supplier => supplier.IsActive)
                .OrderBy(supplier => supplier.SupplierName).ThenBy(supplier => supplier.SupplierId)
                .Select(supplier => new { supplier.SupplierId, supplier.SupplierCode, supplier.SupplierName })
                .ToListAsync(cancellationToken);

            using var package = new ExcelPackage();
            var upload = CreateTemplateSheet(package.Workbook, "GL Upload", ModuleTypeCsvHeader.Split(','));
            upload.Column(1).Style.Numberformat.Format = "yyyy-MM-dd";
            upload.Column(2).Style.Numberformat.Format = "@";
            upload.Column(3).Width = 55;
            upload.Column(4).Style.Numberformat.Format = "@";
            upload.Column(5).Width = 55;
            upload.Column(6).Style.Numberformat.Format = "0.0000";
            upload.Column(7).Style.Numberformat.Format = "0.0000";
            upload.Column(9).Style.Numberformat.Format = "0";
            upload.Column(10).Style.Numberformat.Format = "@";
            upload.Column(10).Width = 55;
            upload.Column(12).Style.Numberformat.Format = "0";
            upload.Column(13).Style.Numberformat.Format = "@";
            upload.Column(13).Width = 30;
            var counterparties = upload.DataValidations.AddListValidation($"K2:K{MaximumRows + 1}");
            counterparties.Formula.Values.Add(nameof(CounterpartyType.Customer));
            counterparties.Formula.Values.Add(nameof(CounterpartyType.Supplier));
            counterparties.AllowBlank = true;
            counterparties.ShowErrorMessage = true;
            counterparties.ErrorTitle = "Invalid counterparty type";
            counterparties.Error = "Select Customer or Supplier, or leave both counterparty fields blank.";
            var types = upload.DataValidations.AddListValidation($"H2:H{MaximumRows + 1}");
            types.Formula.Values.Add(nameof(SubAccountType.Supplier));
            types.Formula.Values.Add(nameof(SubAccountType.Customer));
            types.AllowBlank = true;
            types.ShowErrorMessage = true;
            types.ErrorTitle = "Invalid sub-account type";
            types.Error = "Select Supplier or Customer, or leave both sub-account fields blank.";

            var accountSheet = CreateTemplateSheet(package.Workbook, "Chart of Accounts", ["AccountNo", "AccountName", "MainAccountNo", "MainAccountName"]);
            accountSheet.Column(1).Style.Numberformat.Format = "@";
            accountSheet.Column(2).Width = 70;
            if (accounts.Count > 0)
            {
                accountSheet.Cells[2, 1].LoadFromArrays(accounts.Select(account => new object[]
                {
                    account.AccountNumber, account.AccountName, account.ParentAccountNo, account.ParentAccountName
                }));
            }

            var customerSheet = CreateTemplateSheet(package.Workbook, "Customers",
                ["SubAccountType", "SubAccountId", "CustomerCode", "CustomerName"]);
            customerSheet.Column(3).Style.Numberformat.Format = "@";
            customerSheet.Column(4).Width = 70;
            if (customers.Count > 0)
            {
                customerSheet.Cells[2, 1].LoadFromArrays(customers.Select(customer => new object[]
                {
                    nameof(SubAccountType.Customer), customer.CustomerId, customer.CustomerCode ?? string.Empty, customer.CustomerName
                }));
            }

            var supplierSheet = CreateTemplateSheet(package.Workbook, "Suppliers",
                ["SubAccountType", "SubAccountId", "SupplierCode", "SupplierName"]);
            supplierSheet.Column(3).Style.Numberformat.Format = "@";
            supplierSheet.Column(4).Width = 70;
            if (suppliers.Count > 0)
            {
                supplierSheet.Cells[2, 1].LoadFromArrays(suppliers.Select(supplier => new object[]
                {
                    nameof(SubAccountType.Supplier), supplier.SupplierId, supplier.SupplierCode ?? string.Empty, supplier.SupplierName
                }));
            }

            foreach (var reference in new[] { accountSheet, customerSheet, supplierSheet })
            {
                reference.Cells[reference.Dimension.Address].AutoFilter = true;
            }

            var instructions = CreateTemplateSheet(package.Workbook, "Instructions", ["Step or column", "What to do"]);
            instructions.Column(1).Width = 32;
            instructions.Column(1).Style.WrapText = true;
            instructions.Column(2).Width = 110;
            instructions.Column(2).Style.WrapText = true;
            instructions.Cells[2, 1].LoadFromArrays(new object[][]
            {
                ["1. Fill GL Upload", "Start at row 2 on GL Upload. Keep all 13 column headings in row 1 exactly as they are and in the same order. Enter one debit or credit entry per row. Do not add total or subtotal rows. The column guide below explains what to enter."],
                ["2. Check each transaction", "Rows with the same Company, ModuleType, and Reference belong to one transaction. Use the same date on all of them. Their total debit must equal their total credit. For example, a debit of 1000.00 and a credit of 1000.00 on two rows balance one transaction. Each transaction must balance on its own."],
                ["3. Save a CSV copy", "Save this Excel workbook first so you can edit it later. Select GL Upload, then choose File > Save As or Save a Copy. Choose CSV UTF-8 (Comma delimited) (*.csv). If Excel asks, save only the active sheet and keep the CSV format. Keep your .xlsx workbook too."],
                ["4. Preview and confirm", "Open GL Import from the main menu. Choose your saved .csv file and click Check file and preview. This checks the file without saving entries. Review the dates, accounts, customer/supplier names, and amounts, then click Confirm and save GL entries within 15 minutes."],
                ["If errors appear", "No entries are saved when a file fails the checks. Use the row numbers in the messages to find and fix entries in your Excel workbook. Save GL Upload as CSV UTF-8 again, then upload the new CSV. If the preview expires, upload your CSV again."],
                ["Date (required)", "Use year-month-day, for example 2026-01-15. All rows belonging to one transaction must use the same date."],
                ["Reference (required)", "Enter the transaction or document number, for example JV-0001 (up to 20 characters). Repeat it on every row belonging to that transaction."],
                ["Description (required)", "Enter a short explanation, for example Office supplies purchased."],
                ["AccountNo / AccountName (required)", "Copy both values from the same row of Chart of Accounts. Keep the account name's spelling and capital letters exactly as listed. Keep any zeros at the start of the account number; do not change the AccountNo column's Text format."],
                ["Debit / Credit (required)", "Enter a positive amount on one side and 0 on the other. Use numbers such as 1000.00, without commas or currency symbols. Up to four decimal places are allowed. Do not leave the unused side blank."],
                ["Company (required)", "Type the company name on every row (up to 50 characters). Use the same spelling and capital letters in every upload. You do not need to register the company first."],
                ["ModuleType (required)", "Enter the transaction category on every row (up to 50 characters), for example Sales, Purchase, Disbursement, DebitMemo, CreditMemo, Collection, or Journal. Other names are allowed. Use the same spelling and capital letters for the same category in every upload."],
                ["SubAccountType / SubAccountId (optional)", "Use these for the customer or supplier whose individual ledger this entry belongs to. Find the name on Customers or Suppliers, then copy that row's SubAccountType and SubAccountId. Fill both fields or leave both blank. The system fills in the name."],
                ["CounterpartyType / CounterpartyId (optional)", "Use these for the customer or supplier involved in the transaction: customer for sales, supplier for purchases. On Customers or Suppliers, copy SubAccountType into CounterpartyType and SubAccountId into CounterpartyId. Fill both fields or leave both blank. The system fills in the name."],
                ["Which ID do I copy?", "Use the number under SubAccountId on Customers or Suppliers, not CustomerCode or SupplierCode. Copy the type from the same row: Customer or Supplier. The same number can belong to a different customer and supplier, so the type matters."],
                ["Are the optional pairs the same?", "They are separate and may refer to different records. Filling in one does not fill in the other. For a supplier purchase, the expense row can have its sub-account pair blank and its counterparty set to the supplier. The payable row can use that supplier in both pairs."],
                ["Uploading a correction", "Include every corrected row for the transaction, even rows that have not changed. Use the original Company, ModuleType, and Reference with the same spelling and capital letters. Confirming replaces ALL old entries for that transaction with the rows in your file. The date may change."],
                ["Before confirming a correction", "Old entries are permanently deleted and cannot be restored from import history. Changing Company, ModuleType, or Reference creates a separate transaction instead. Transactions not included in the file stay unchanged. The same unchanged file cannot be imported twice."],
                ["File limits", "Upload a .csv file, not this .xlsx workbook. Each CSV may contain up to 50,000 entry rows and be up to 10 MB. If you need more than one file, keep all rows belonging to each transaction together in the same file."],
                ["Keeping reference lists current", "Chart of Accounts, Customers, and Suppliers show the available accounts and active customers/suppliers when this template was downloaded. Download a fresh template if those lists have changed. The system checks the current records again before saving."],
                ["After importing", "Check the imported entries against your source records. Once all entries are imported and checked, coordinate with MIS for month-end closing. MIS is responsible for locking and closing the months from earliest to latest. Reports that use closed balances need this step."]
            });
            instructions.Cells[instructions.Dimension.Address].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
            instructions.Rows[2, instructions.Dimension.End.Row].Height = 75;
            package.Workbook.View.ActiveTab = 0;
            return await package.GetAsByteArrayAsync(cancellationToken);
        }

        private static ExcelWorksheet CreateTemplateSheet(ExcelWorkbook workbook, string name, string[] headers)
        {
            var sheet = workbook.Worksheets.Add(name);
            sheet.Cells[1, 1].LoadFromArrays(new[] { headers.Cast<object>().ToArray() });
            sheet.Cells[1, 1, 1, headers.Length].Style.Font.Bold = true;
            sheet.Cells[1, 1, 1, headers.Length].Style.Font.Color.SetColor(Color.White);
            sheet.Cells[1, 1, 1, headers.Length].Style.Fill.PatternType = ExcelFillStyle.Solid;
            sheet.Cells[1, 1, 1, headers.Length].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(31, 78, 120));
            sheet.Cells[1, 1, 1, headers.Length].Style.WrapText = true;
            sheet.Row(1).Height = 30;
            sheet.View.FreezePanes(2, 1);
            for (var column = 1; column <= headers.Length; column++)
            {
                sheet.Column(column).Width = 22;
            }
            return sheet;
        }

        public async Task<GlImportPreview> PreviewAsync(byte[] contents, string fileName,
            CancellationToken cancellationToken = default)
        {
            var result = await PrepareAsync(contents, fileName, cancellationToken);
            return result.Preview;
        }

        private async Task<(GlImportPreview Preview, List<FilprideGeneralLedgerBook> ExistingRows)> PrepareAsync(
            byte[] contents, string fileName, CancellationToken cancellationToken)
        {
            var preview = Parse(contents, fileName, cancellationToken);
            if (!preview.CanImport)
            {
                return (preview, []);
            }

            foreach (var journal in preview.Rows.GroupBy(row => (row.Company, row.ModuleType, row.Reference)))
            {
                if (journal.Select(row => row.Date).Distinct().Skip(1).Any())
                {
                    AddError(preview, $"Transaction {journal.Key.Company}/{journal.Key.ModuleType}/{journal.Key.Reference}: use the same Date on every row belonging to this transaction.");
                }
                var debit = journal.Sum(row => row.Debit);
                var credit = journal.Sum(row => row.Credit);
                if (debit != credit)
                {
                    AddError(preview, $"Transaction {journal.Key.Company}/{journal.Key.ModuleType}/{journal.Key.Reference}: total debit ({debit:N4}) must equal total credit ({credit:N4}). Check all rows belonging to this transaction.");
                }
            }

            await ValidateAccountsAsync(preview, cancellationToken);
            await ValidateSubAccountsAsync(preview, cancellationToken);
            await ValidateCounterpartiesAsync(preview, cancellationToken);
            var existingRows = await FindReplacementRowsAsync(preview, cancellationToken);
            var firstDate = preview.Rows.Select(row => row.Date)
                .Concat(existingRows.Select(row => row.Date)).Min();
            await ValidatePeriodsAsync(preview, firstDate, cancellationToken);
            return (preview, existingRows);
        }

        public async Task<Guid> ImportAsync(byte[] contents, string fileName, string user,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(user) || user.Length > 100)
            {
                throw new InvalidOperationException("The importing username must contain 1 to 100 characters.");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            await _dbContext.Database.ExecuteSqlRawAsync(PeriodLockSql, cancellationToken);
            var (preview, existingRows) = await PrepareAsync(contents, fileName, cancellationToken);
            if (!preview.CanImport)
            {
                throw new InvalidOperationException(preview.Errors.FirstOrDefault() ?? "The file contains no GL lines.");
            }

            var batch = new FilprideGlImportBatch
            {
                Id = Guid.NewGuid(),
                FileName = preview.FileName,
                FileHash = preview.FileHash,
                ImportedBy = user,
                ImportedAt = DateTimeHelper.GetCurrentPhilippineTime(),
                RowCount = preview.Rows.Count,
                JournalCount = preview.JournalCount,
                DateFrom = preview.Rows.Min(row => row.Date),
                DateTo = preview.Rows.Max(row => row.Date),
                TotalDebit = preview.TotalDebit,
                TotalCredit = preview.TotalCredit
            };
            _dbContext.FilprideGeneralLedgerBooks.RemoveRange(existingRows);
            _dbContext.FilprideGlImportBatches.Add(batch);
            _dbContext.FilprideGeneralLedgerBooks.AddRange(preview.Rows.Select(row => new FilprideGeneralLedgerBook
            {
                Date = row.Date,
                Reference = row.Reference,
                Description = row.Description,
                Company = row.Company,
                CounterpartyType = row.CounterpartyType,
                CounterpartyId = row.CounterpartyId,
                CounterpartyName = row.CounterpartyName,
                AccountId = row.AccountId,
                AccountNo = row.AccountNo,
                AccountTitle = row.AccountName,
                Debit = row.Debit,
                Credit = row.Credit,
                CreatedBy = user,
                CreatedDate = batch.ImportedAt,
                IsPosted = true,
                ModuleType = row.ModuleType,
                ImportBatchId = batch.Id,
                SubAccountType = row.SubAccountType,
                SubAccountId = row.SubAccountId,
                SubAccountName = row.SubAccountName
            }));
            _dbContext.FilprideAuditTrails.Add(new FilprideAuditTrail(user,
                $"Imported GL batch {batch.Id}: {batch.RowCount} lines, {batch.JournalCount} journals. Deleted {existingRows.Count} previous imported GL lines.", "GL Import"));
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return batch.Id;
        }

        public async Task<List<FilprideGlImportBatch>> GetHistoryAsync(CancellationToken cancellationToken = default)
        {
            return await _dbContext.FilprideGlImportBatches.AsNoTracking()
                .OrderByDescending(batch => batch.ImportedAt).Take(50).ToListAsync(cancellationToken);
        }

        private async Task ValidateAccountsAsync(GlImportPreview preview, CancellationToken cancellationToken)
        {
            Dictionary<string, FilprideChartOfAccount> accounts = await _dbContext.FilprideChartOfAccounts
                .AsNoTracking().ToDictionaryAsync(account => account.AccountNumber, cancellationToken);
            foreach (GlImportRow row in preview.Rows)
            {
                if (!accounts.TryGetValue(row.AccountNo, out FilprideChartOfAccount? account))
                {
                    AddError(preview, $"Row {row.RowNumber}: account {row.AccountNo} is not available. Download a fresh template and copy AccountNo and AccountName from Chart of Accounts.");
                }
                else if (account.HasChildren)
                {
                    AddError(preview, $"Row {row.RowNumber}: account {row.AccountNo} is a summary account and cannot be used for entries. Copy an account from Chart of Accounts in the template.");
                }
                else if (!string.Equals(row.AccountName, account.AccountName, StringComparison.Ordinal))
                {
                    AddError(preview, $"Row {row.RowNumber}: AccountName for {row.AccountNo} must be '{account.AccountName}'.");
                }
                else
                {
                    row.AccountId = account.AccountId;
                }
            }
        }

        private async Task ValidateSubAccountsAsync(GlImportPreview preview, CancellationToken cancellationToken)
        {
            var groups = preview.Rows.Where(row => row.SubAccountType.HasValue && row.SubAccountId.HasValue)
                .GroupBy(row => (Type: row.SubAccountType!.Value, Id: row.SubAccountId!.Value)).ToList();
            if (groups.Count == 0)
            {
                return;
            }

            var supplierIds = groups.Where(group => group.Key.Type == SubAccountType.Supplier)
                .Select(group => group.Key.Id).ToArray();
            var customerIds = groups.Where(group => group.Key.Type == SubAccountType.Customer)
                .Select(group => group.Key.Id).ToArray();
            var activeSupplierIds = await _dbContext.FilprideSuppliers.AsNoTracking()
                .Where(supplier => supplier.IsActive && supplierIds.Contains(supplier.SupplierId))
                .Select(supplier => supplier.SupplierId).ToHashSetAsync(cancellationToken);
            var activeCustomerIds = await _dbContext.FilprideCustomers.AsNoTracking()
                .Where(customer => customer.IsActive && customerIds.Contains(customer.CustomerId))
                .Select(customer => customer.CustomerId).ToHashSetAsync(cancellationToken);

            foreach (var group in groups)
            {
                var isActive = group.Key.Type == SubAccountType.Supplier
                    ? activeSupplierIds.Contains(group.Key.Id)
                    : activeCustomerIds.Contains(group.Key.Id);
                var subAccount = isActive
                    ? await _subAccountResolver.ResolveAsync(group.Key.Type, group.Key.Id, cancellationToken)
                    : null;
                foreach (var row in group)
                {
                    if (subAccount == null)
                    {
                        AddError(preview, $"Row {row.RowNumber}: {group.Key.Type} ID {group.Key.Id} does not exist or is inactive.");
                    }
                    else if (ValidateText(subAccount.Name, "SubAccountName", 200, row.RowNumber, preview))
                    {
                        row.SubAccountName = subAccount.Name;
                    }
                }
            }
        }

        private async Task ValidateCounterpartiesAsync(GlImportPreview preview, CancellationToken cancellationToken)
        {
            var rows = preview.Rows.Where(row => row.CounterpartyType.HasValue && row.CounterpartyId.HasValue).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            var customerIds = rows.Where(row => row.CounterpartyType == CounterpartyType.Customer)
                .Select(row => row.CounterpartyId!.Value).Distinct().ToArray();
            var supplierIds = rows.Where(row => row.CounterpartyType == CounterpartyType.Supplier)
                .Select(row => row.CounterpartyId!.Value).Distinct().ToArray();
            var customers = await _dbContext.FilprideCustomers.AsNoTracking()
                .Where(customer => customer.IsActive && customerIds.Contains(customer.CustomerId))
                .ToDictionaryAsync(customer => customer.CustomerId, customer => customer.CustomerName, cancellationToken);
            var suppliers = await _dbContext.FilprideSuppliers.AsNoTracking()
                .Where(supplier => supplier.IsActive && supplierIds.Contains(supplier.SupplierId))
                .ToDictionaryAsync(supplier => supplier.SupplierId, supplier => supplier.SupplierName, cancellationToken);

            foreach (var row in rows)
            {
                var names = row.CounterpartyType == CounterpartyType.Customer ? customers : suppliers;
                if (!names.TryGetValue(row.CounterpartyId!.Value, out var name))
                {
                    AddError(preview, $"Row {row.RowNumber}: counterparty {row.CounterpartyType} ID {row.CounterpartyId} does not exist or is inactive.");
                }
                else if (ValidateText(name, "CounterpartyName", 200, row.RowNumber, preview))
                {
                    row.CounterpartyName = name;
                }
            }
        }

        private async Task ValidatePeriodsAsync(GlImportPreview preview, DateOnly firstDate, CancellationToken cancellationToken)
        {
            var hasAffectedClosedBalances = await _dbContext.FilprideGlPeriodBalances.IgnoreQueryFilters()
                .AnyAsync(period => period.IsValid && period.IsClosed && period.PeriodEndDate >= firstDate, cancellationToken);
            if (hasAffectedClosedBalances)
            {
                AddError(preview, "Reopen the affected month and all later closed months before importing historical entries.");
            }
        }

        private async Task<List<FilprideGeneralLedgerBook>> FindReplacementRowsAsync(GlImportPreview preview,
            CancellationToken cancellationToken)
        {
            if (await _dbContext.FilprideGlImportBatches.AnyAsync(batch => batch.FileHash == preview.FileHash, cancellationToken))
            {
                AddError(preview, "This file has already been imported.");
                return [];
            }

            var journalKeys = preview.Rows.Select(row => (row.Company, row.ModuleType, row.Reference)).ToHashSet();
            var existingRows = new List<FilprideGeneralLedgerBook>();
            foreach (var references in preview.Rows.Select(row => row.Reference).Distinct().Chunk(500))
            {
                var candidates = await _dbContext.FilprideGeneralLedgerBooks.AsNoTracking()
                    .Where(gl => references.Contains(gl.Reference.Trim()))
                    .ToListAsync(cancellationToken);
                existingRows.AddRange(candidates.Where(gl => gl.Company != null &&
                    journalKeys.Contains((gl.Company.Trim(), gl.ModuleType.Trim(), gl.Reference.Trim()))));
            }
            foreach (var journal in existingRows.GroupBy(gl => (Company: gl.Company!.Trim(),
                         ModuleType: gl.ModuleType.Trim(), Reference: gl.Reference.Trim())))
            {
                if (journal.Any(gl => !gl.ImportBatchId.HasValue))
                {
                    AddError(preview, $"Transaction {journal.Key.Company}/{journal.Key.ModuleType}/{journal.Key.Reference} contains non-imported GL entries and cannot be replaced.");
                }
                else if (journal.Select(gl => (gl.Date, gl.ImportBatchId)).Distinct().Skip(1).Any())
                {
                    AddError(preview, $"Transaction {journal.Key.Company}/{journal.Key.ModuleType}/{journal.Key.Reference} spans multiple existing dates or import batches and cannot be replaced.");
                }
            }
            return existingRows;
        }

        public static GlImportPreview Parse(byte[] contents, string fileName, CancellationToken cancellationToken = default)
        {
            var preview = new GlImportPreview
            {
                FileName = Path.GetFileName(fileName.Replace('\\', '/')),
                FileHash = Convert.ToHexString(SHA256.HashData(contents))
            };
            if (contents.Length == 0 || contents.Length > MaximumFileBytes)
            {
                AddError(preview, "Choose a CSV file that contains entries and is no larger than 10 MB. Save the GL Upload sheet as CSV UTF-8 in Excel.");
                return preview;
            }
            if (preview.FileName.Length is 0 or > 255 ||
                !string.Equals(Path.GetExtension(preview.FileName), ".csv", StringComparison.OrdinalIgnoreCase))
            {
                AddError(preview, "Select a .csv file with a filename no longer than 255 characters.");
                return preview;
            }

            try
            {
                using var stream = new MemoryStream(contents);
                using var reader = new StreamReader(stream, new UTF8Encoding(false, true), false);
                using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
                {
                    TrimOptions = TrimOptions.Trim,
                    ExceptionMessagesContainRawData = false
                });
                if (!csv.Read())
                {
                    AddError(preview, "The file is empty.");
                    return preview;
                }
                csv.ReadHeader();
                string[] headers = csv.HeaderRecord ?? [];
                if (headers.Length > 0)
                {
                    headers[0] = headers[0].TrimStart('\uFEFF');
                }
                var baseHeaders = headers.LastOrDefault() == "ModuleType" ? headers[..^1] : headers;
                if (!baseHeaders.SequenceEqual((CsvHeader + ",Company").Split(',')) &&
                    !baseHeaders.SequenceEqual(CompanyCsvHeader.Split(',')) &&
                    !baseHeaders.SequenceEqual((CsvHeader + ",Company,CounterpartyType,CounterpartyId").Split(',')) &&
                    !baseHeaders.SequenceEqual(CounterpartyCsvHeader.Split(',')))
                {
                    AddError(preview, "The column headings or their order do not match the upload format. Download a fresh Excel template, copy your entries below row 1 without changing or removing its 13 headings, then save only GL Upload as CSV UTF-8.");
                    return preview;
                }

                int rowNumber = 1;
                while (csv.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rowNumber++;
                    if (rowNumber > (MaximumRows + 1))
                    {
                        AddError(preview, $"A file may contain at most {MaximumRows:N0} GL lines.");
                        break;
                    }
                    if (csv.Parser.Count != headers.Length)
                    {
                        AddError(preview, $"Row {rowNumber}: this row must have {headers.Length} columns, including blank optional cells. Check the row in your workbook, then save GL Upload as CSV UTF-8 again.");
                        continue;
                    }
                    ReadRow(csv, rowNumber, preview);
                }
            }
            catch (CsvHelperException)
            {
                AddError(preview, "The file could not be read as CSV. Open your Excel workbook and save only the GL Upload sheet as CSV UTF-8 (Comma delimited), then upload the new CSV.");
            }
            catch (DecoderFallbackException)
            {
                AddError(preview, "In Excel, save GL Upload using the file type CSV UTF-8 (Comma delimited), then upload the new CSV.");
            }
            if (preview.Rows.Count == 0)
            {
                AddError(preview, "The file contains no valid GL lines.");
            }
            return preview;
        }

        private static void ReadRow(CsvReader csv, int rowNumber, GlImportPreview preview)
        {
            var fields = Enumerable.Range(0, csv.Parser.Count).Select(column => (csv.GetField(column) ?? string.Empty).Trim()).ToArray();
            bool valid = DateOnly.TryParseExact(fields[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date);
            if (!valid)
            {
                AddError(preview, $"Row {rowNumber}: enter Date as year-month-day, for example 2026-01-15.");
            }
            valid = ValidateText(fields[1], "Reference", 20, rowNumber, preview) && valid;
            valid = ValidateText(fields[2], "Description", int.MaxValue, rowNumber, preview) && valid;
            valid = ValidateText(fields[3], "AccountNo", 50, rowNumber, preview) && valid;
            valid = ValidateText(fields[4], "AccountName", 200, rowNumber, preview) && valid;
            var headers = csv.HeaderRecord!;
            var hasSubAccounts = headers.Contains("SubAccountType");
            var company = fields[hasSubAccounts ? 9 : 7];
            valid = ValidateText(company, "Company", 50, rowNumber, preview) && valid;
            var moduleType = headers.Contains("ModuleType") ? fields[^1] : "LegacyGL";
            valid = ValidateText(moduleType, "ModuleType", 50, rowNumber, preview) && valid;
            bool hasDebit = TryParseAmount(fields[5], out decimal debit);
            bool hasCredit = TryParseAmount(fields[6], out decimal credit);
            if (!hasDebit || !hasCredit)
            {
                AddError(preview, $"Row {rowNumber}: Debit and Credit must be nonnegative numbers with at most four decimal places, no thousands separators, and at most 14 integer digits.");
                valid = false;
            }
            else if ((debit > 0) == (credit > 0))
            {
                AddError(preview, $"Row {rowNumber}: enter a positive amount on exactly one side and zero on the other.");
                valid = false;
            }
            var subAccountType = (SubAccountType?)null;
            var subAccountId = (int?)null;
            if (hasSubAccounts && (fields[7].Length > 0 || fields[8].Length > 0))
            {
                subAccountType = fields[7] switch
                {
                    nameof(SubAccountType.Supplier) => SubAccountType.Supplier,
                    nameof(SubAccountType.Customer) => SubAccountType.Customer,
                    _ => null
                };
                if (!subAccountType.HasValue ||
                    !int.TryParse(fields[8], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    AddError(preview, $"Row {rowNumber}: copy SubAccountType (Supplier or Customer) and SubAccountId from the same row of Suppliers or Customers in the template. Use the ID number, not the code. If no sub-account applies, leave both cells blank.");
                    valid = false;
                }
                else
                {
                    subAccountId = id;
                }
            }
            var counterpartyType = (CounterpartyType?)null;
            var counterpartyId = (int?)null;
            var counterpartyIndex = Array.IndexOf(headers, "CounterpartyType");
            if (counterpartyIndex >= 0 && (fields[counterpartyIndex].Length > 0 || fields[counterpartyIndex + 1].Length > 0))
            {
                counterpartyType = fields[counterpartyIndex] switch
                {
                    nameof(CounterpartyType.Customer) => CounterpartyType.Customer,
                    nameof(CounterpartyType.Supplier) => CounterpartyType.Supplier,
                    _ => null
                };
                if (!counterpartyType.HasValue ||
                    !int.TryParse(fields[counterpartyIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    AddError(preview, $"Row {rowNumber}: copy SubAccountType and SubAccountId from Customers or Suppliers into CounterpartyType (Customer or Supplier) and CounterpartyId. Use the ID number, not the code. If no counterparty applies, leave both cells blank.");
                    valid = false;
                }
                else
                {
                    counterpartyId = id;
                }
            }
            if (!valid)
            {
                return;
            }
            preview.Rows.Add(new GlImportRow
            {
                RowNumber = rowNumber,
                Date = date,
                Reference = fields[1],
                Description = fields[2],
                AccountNo = fields[3],
                AccountName = fields[4],
                Company = company,
                ModuleType = moduleType,
                CounterpartyType = counterpartyType,
                CounterpartyId = counterpartyId,
                Debit = debit,
                Credit = credit,
                SubAccountType = subAccountType,
                SubAccountId = subAccountId
            });
        }

        private static bool ValidateText(string value, string column, int maximumLength, int rowNumber, GlImportPreview preview)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddError(preview, $"Row {rowNumber}: {column} is required.");
                return false;
            }
            if (value.Length > maximumLength)
            {
                AddError(preview, $"Row {rowNumber}: {column} cannot exceed {maximumLength} characters.");
                return false;
            }
            if (value.Contains('\0'))
            {
                AddError(preview, $"Row {rowNumber}: {column} cannot contain NUL characters.");
                return false;
            }
            return true;
        }

        private static bool TryParseAmount(string text, out decimal amount)
        {
            return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
                   && (amount >= 0) && (amount <= 99999999999999.9999m) && (decimal.Round(amount, 4) == amount);
        }

        private static void AddError(GlImportPreview preview, string error)
        {
            if (preview.Errors.Count < MaximumErrors)
            {
                preview.Errors.Add(error);
            }
        }
    }
}
