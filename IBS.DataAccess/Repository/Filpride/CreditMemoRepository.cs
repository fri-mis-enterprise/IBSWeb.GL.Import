using System.Linq.Expressions;
using IBS.DataAccess.Data;
using IBS.DataAccess.Repository.Filpride.IRepository;
using IBS.DTOs;
using IBS.Models.Enums;
using IBS.Models.Filpride.AccountsReceivable;
using IBS.Models.Filpride.Books;
using IBS.Utility.Constants;
using IBS.Utility.Helpers;
using Microsoft.EntityFrameworkCore;

namespace IBS.DataAccess.Repository.Filpride
{
    public class CreditMemoRepository: Repository<FilprideCreditMemo>, ICreditMemoRepository
    {
        private readonly ApplicationDbContext _db;

        public CreditMemoRepository(ApplicationDbContext db) : base(db)
        {
            _db = db;
        }

        public async Task<string> GenerateCodeAsync(string type, CancellationToken cancellationToken = default)
        {
            return type switch
            {
                nameof(DocumentType.Documented) => await GenerateCodeForDocumented(cancellationToken),
                nameof(DocumentType.Undocumented) => await GenerateCodeForUnDocumented(cancellationToken),
                _ => throw new ArgumentException("Invalid type")
            };
        }

        private async Task<string> GenerateCodeForDocumented(CancellationToken cancellationToken = default)
        {
            var lastCm = await _db
                .FilprideCreditMemos
                .AsNoTracking()
                .OrderByDescending(x => x.CreditMemoNo!.Length)
                .ThenByDescending(x => x.CreditMemoNo)
                .FirstOrDefaultAsync(x =>

                    x.Type == nameof(DocumentType.Documented),
                    cancellationToken);

            if (lastCm == null)
            {
                return "CM0000000001";
            }

            var lastSeries = lastCm.CreditMemoNo!;
            var numericPart = lastSeries.Substring(2);
            var incrementedNumber = long.Parse(numericPart) + 1;

            return lastSeries.Substring(0, 2) + incrementedNumber.ToString("D10");
        }

        private async Task<string> GenerateCodeForUnDocumented(CancellationToken cancellationToken = default)
        {
            var lastCm = await _db
                .FilprideCreditMemos
                .AsNoTracking()
                .OrderByDescending(x => x.CreditMemoNo)
                .FirstOrDefaultAsync(x =>

                        x.Type == nameof(DocumentType.Undocumented),
                    cancellationToken);

            if (lastCm == null)
            {
                return "CMU000000001";
            }

            var lastSeries = lastCm.CreditMemoNo!;
            var numericPart = lastSeries.Substring(3);
            var incrementedNumber = long.Parse(numericPart) + 1;

            return lastSeries.Substring(0, 3) + incrementedNumber.ToString("D9");
        }

        public async Task CalculateTaxAmountsAsync(FilprideCreditMemo model,
            CancellationToken cancellationToken = default)
        {
            if (model.SalesInvoiceId.HasValue == model.ServiceInvoiceId.HasValue)
            {
                throw new ArgumentException("Select exactly one sales or service invoice.");
            }

            var creditAmount = -Math.Abs(model.CreditAmount);
            if (model.SalesInvoiceId.HasValue)
            {
                var salesInvoice = await _db.FilprideSalesInvoices
                    .Include(invoice => invoice.CustomerOrderSlip)
                    .Include(invoice => invoice.Customer)
                    .FirstOrDefaultAsync(invoice => invoice.SalesInvoiceId == model.SalesInvoiceId, cancellationToken)
                    ?? throw new ArgumentException("Sales invoice not found.");
                var vatType = salesInvoice.CustomerOrderSlip?.VatType ?? salesInvoice.Customer?.VatType;
                var hasEwt = salesInvoice.CustomerOrderSlip?.HasEWT ?? salesInvoice.Customer?.WithHoldingTax ?? false;
                var hasWvat = salesInvoice.CustomerOrderSlip?.HasWVAT ?? salesInvoice.Customer?.WithHoldingVat ?? false;
                var netOfVatAmount = vatType == SD.VatType_Vatable
                    ? DecimalRoundingHelper.ComputeNetOfVat(creditAmount)
                    : DecimalRoundingHelper.RoundToFour(creditAmount);

                model.CwtAmount = hasEwt
                    ? DecimalRoundingHelper.ComputeEwtAmount(netOfVatAmount, salesInvoice.CwtPercent)
                    : 0m;
                model.CwVatAmount = hasWvat
                    ? DecimalRoundingHelper.ComputeEwtAmount(netOfVatAmount, salesInvoice.CwVatPercent)
                    : 0m;
                return;
            }

            var serviceInvoice = await _db.FilprideServiceInvoices
                .FirstOrDefaultAsync(invoice => invoice.ServiceInvoiceId == model.ServiceInvoiceId, cancellationToken)
                ?? throw new ArgumentException("Service invoice not found.");
            var serviceNetOfVatAmount = serviceInvoice.VatType == SD.VatType_Vatable
                ? DecimalRoundingHelper.ComputeNetOfVat(creditAmount)
                : DecimalRoundingHelper.RoundToFour(creditAmount);

            model.CwtAmount = serviceInvoice.HasEwt
                ? DecimalRoundingHelper.ComputeEwtAmount(serviceNetOfVatAmount, serviceInvoice.ServicePercent / 100m)
                : 0m;
            model.CwVatAmount = serviceInvoice.HasWvat
                ? DecimalRoundingHelper.ComputeEwtAmount(serviceNetOfVatAmount, 0.05m)
                : 0m;
        }

        public async Task PostAsync(FilprideCreditMemo model,
            CancellationToken cancellationToken = default,
            List<AccountTitleDto>? accountTitlesDto = null,
            bool saveChanges = true)
        {
            accountTitlesDto ??= await GetListOfAccountTitleDto(cancellationToken);
            var arTradeReceivableTitle = accountTitlesDto.Find(c => c.AccountNumber == "101020100") ?? throw new ArgumentException("Account title '101020100' not found.");
            var arNonTradeTitle = accountTitlesDto.Find(c => c.AccountNumber == "101020500") ?? throw new ArgumentException("Account title '101020500' not found.");
            var arTradeCwt = accountTitlesDto.Find(c => c.AccountNumber == "101020200") ?? throw new ArgumentException("Account title '101020200' not found.");
            var arTradeCwv = accountTitlesDto.Find(c => c.AccountNumber == "101020300") ?? throw new ArgumentException("Account title '101020300' not found.");
            var vatOutputTitle = accountTitlesDto.Find(c => c.AccountNumber == "201030100") ?? throw new ArgumentException("Account title '201030100' not found.");

            if (model.SalesInvoiceId != null)
            {
                var salesInvoice = model.SalesInvoice
                    ?? throw new ArgumentException("Sales invoice is required.");
                var customerOrderSlip = salesInvoice.CustomerOrderSlip
                    ?? throw new ArgumentException("Customer order slip is required.");
                var product = salesInvoice.Product
                    ?? throw new ArgumentException("Product is required.");

                var (salesAcctNo, _) = GetSalesAccountTitle(product.ProductCode);
                var salesTitle = accountTitlesDto.Find(c => c.AccountNumber == salesAcctNo)
                    ?? throw new ArgumentException($"Account title '{salesAcctNo}' not found.");

                var withHoldingTaxAmount = model.CwtAmount;
                var withHoldingVatAmount = model.CwVatAmount;
                decimal netOfVatAmount;
                decimal vatAmount = 0m;

                if (customerOrderSlip.VatType == SD.VatType_Vatable)
                {
                    netOfVatAmount = ComputeNetOfVat(Math.Abs(model.CreditAmount)) * -1m;
                    vatAmount = ComputeVatAmount(Math.Abs(netOfVatAmount)) * -1m;
                }
                else
                {
                    netOfVatAmount = model.CreditAmount;
                }

                var ledgers = new List<FilprideGeneralLedgerBook>
                {
                    new()
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = customerOrderSlip.ProductName,
                        AccountId = arTradeReceivableTitle.AccountId,
                        AccountNo = arTradeReceivableTitle.AccountNumber,
                        AccountTitle = arTradeReceivableTitle.AccountName,
                        Debit = 0,
                        Credit = Math.Abs(model.CreditAmount - (withHoldingTaxAmount + withHoldingVatAmount)),
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        SubAccountType = SubAccountType.Customer,
                        SubAccountId = customerOrderSlip.CustomerId,
                        SubAccountName = customerOrderSlip.CustomerName,
                        ModuleType = nameof(ModuleType.CreditMemo)
                    }
                };

                if (withHoldingTaxAmount < 0)
                {
                    ledgers.Add(new FilprideGeneralLedgerBook
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = customerOrderSlip.ProductName,
                        AccountId = arTradeCwt.AccountId,
                        AccountNo = arTradeCwt.AccountNumber,
                        AccountTitle = arTradeCwt.AccountName,
                        Debit = 0,
                        Credit = Math.Abs(withHoldingTaxAmount),
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        ModuleType = nameof(ModuleType.CreditMemo)
                    });
                }

                if (withHoldingVatAmount < 0)
                {
                    ledgers.Add(new FilprideGeneralLedgerBook
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = customerOrderSlip.ProductName,
                        AccountId = arTradeCwv.AccountId,
                        AccountNo = arTradeCwv.AccountNumber,
                        AccountTitle = arTradeCwv.AccountName,
                        Debit = 0,
                        Credit = Math.Abs(withHoldingVatAmount),
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        ModuleType = nameof(ModuleType.CreditMemo)
                    });
                }

                ledgers.Add(new FilprideGeneralLedgerBook
                {
                    Date = model.TransactionDate,
                    Reference = model.CreditMemoNo!,
                    Description = customerOrderSlip.ProductName,
                    AccountId = salesTitle.AccountId,
                    AccountNo = salesTitle.AccountNumber,
                    AccountTitle = salesTitle.AccountName,
                    Debit = Math.Abs(netOfVatAmount),
                    Credit = 0,
                    CreatedBy = model.PostedBy!,
                    CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                    ModuleType = nameof(ModuleType.CreditMemo)
                });

                if (vatAmount < 0)
                {
                    ledgers.Add(new FilprideGeneralLedgerBook
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = customerOrderSlip.ProductName,
                        AccountId = vatOutputTitle.AccountId,
                        AccountNo = vatOutputTitle.AccountNumber,
                        AccountTitle = vatOutputTitle.AccountName,
                        Debit = Math.Abs(vatAmount),
                        Credit = 0,
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        ModuleType = nameof(ModuleType.CreditMemo)
                    });
                }

                if (!IsJournalEntriesBalanced(ledgers))
                {
                    throw new ArgumentException("Debit and Credit is not equal, check your entries.");
                }

                ledgers.SetCounterparty(
                    CounterpartyType.Customer,
                    customerOrderSlip.CustomerId,
                    customerOrderSlip.CustomerName);

                await _db.FilprideGeneralLedgerBooks.AddRangeAsync(ledgers, cancellationToken);
            }

            if (model.ServiceInvoiceId != null)
            {
                var serviceInvoice = model.ServiceInvoice
                    ?? throw new ArgumentException("Service invoice is required.");
                var service = serviceInvoice.Service
                    ?? throw new ArgumentException("Service is required.");
                var serviceTitle = accountTitlesDto.Find(c => c.AccountNumber == service.CurrentAndPreviousNo)
                    ?? throw new ArgumentException($"Account title '{service.CurrentAndPreviousNo}' not found.");

                var netAmount = serviceInvoice.VatType == SD.VatType_Vatable
                    ? ComputeNetOfVat(Math.Abs(model.CreditAmount))
                    : Math.Abs(model.CreditAmount);

                var withHoldingTaxAmount = model.CwtAmount;
                var withHoldingVatAmount = model.CwVatAmount;
                decimal netOfVatAmount = 0m;
                decimal vatAmount = 0m;

                if (serviceInvoice.VatType == SD.VatType_Vatable)
                {
                    netOfVatAmount = ComputeNetOfVat(Math.Abs(model.CreditAmount)) * -1m;
                    vatAmount = ComputeVatAmount(Math.Abs(netOfVatAmount)) * -1m;
                }
                else
                {
                    netOfVatAmount = model.CreditAmount;
                }

                var ledgers = new List<FilprideGeneralLedgerBook>
                {
                    new()
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = serviceInvoice.ServiceName,
                        AccountId = arNonTradeTitle.AccountId,
                        AccountNo = arNonTradeTitle.AccountNumber,
                        AccountTitle = arNonTradeTitle.AccountName,
                        Debit = 0,
                        Credit = Math.Abs(model.CreditAmount - (withHoldingTaxAmount + withHoldingVatAmount)),
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        SubAccountType = SubAccountType.Customer,
                        SubAccountId = serviceInvoice.CustomerId,
                        SubAccountName = serviceInvoice.CustomerName,
                        ModuleType = nameof(ModuleType.CreditMemo)
                    }
                };

                if (withHoldingTaxAmount < 0)
                {
                    ledgers.Add(new FilprideGeneralLedgerBook
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = serviceInvoice.ServiceName,
                        AccountId = arTradeCwt.AccountId,
                        AccountNo = arTradeCwt.AccountNumber,
                        AccountTitle = arTradeCwt.AccountName,
                        Debit = 0,
                        Credit = Math.Abs(withHoldingTaxAmount),
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        ModuleType = nameof(ModuleType.CreditMemo)
                    });
                }

                if (withHoldingVatAmount < 0)
                {
                    ledgers.Add(new FilprideGeneralLedgerBook
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = serviceInvoice.ServiceName,
                        AccountId = arTradeCwv.AccountId,
                        AccountNo = arTradeCwv.AccountNumber,
                        AccountTitle = arTradeCwv.AccountName,
                        Debit = 0,
                        Credit = Math.Abs(withHoldingVatAmount),
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        ModuleType = nameof(ModuleType.CreditMemo)
                    });
                }

                ledgers.Add(new FilprideGeneralLedgerBook
                {
                    Date = model.TransactionDate,
                    Reference = model.CreditMemoNo!,
                    Description = serviceInvoice.ServiceName,
                    AccountId = serviceTitle.AccountId,
                    AccountNo = serviceTitle.AccountNumber,
                    AccountTitle = serviceTitle.AccountName,
                    Debit = netAmount,
                    Credit = 0,
                    CreatedBy = model.PostedBy!,
                    CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                    ModuleType = nameof(ModuleType.CreditMemo)
                });

                if (vatAmount < 0)
                {
                    ledgers.Add(new FilprideGeneralLedgerBook
                    {
                        Date = model.TransactionDate,
                        Reference = model.CreditMemoNo!,
                        Description = serviceInvoice.ServiceName,
                        AccountId = vatOutputTitle.AccountId,
                        AccountNo = vatOutputTitle.AccountNumber,
                        AccountTitle = vatOutputTitle.AccountName,
                        Debit = Math.Abs(vatAmount),
                        Credit = 0,
                        CreatedBy = model.PostedBy!,
                        CreatedDate = DateTimeHelper.GetCurrentPhilippineTime(),
                        ModuleType = nameof(ModuleType.CreditMemo)
                    });
                }

                if (!IsJournalEntriesBalanced(ledgers))
                {
                    throw new ArgumentException("Debit and Credit is not equal, check your entries.");
                }

                ledgers.SetCounterparty(
                    CounterpartyType.Customer,
                    serviceInvoice.CustomerId,
                    serviceInvoice.CustomerName);

                await _db.FilprideGeneralLedgerBooks.AddRangeAsync(ledgers, cancellationToken);
            }

            if (saveChanges)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        public override async Task<FilprideCreditMemo?> GetAsync(Expression<Func<FilprideCreditMemo, bool>> filter, CancellationToken cancellationToken = default)
        {
            return await dbSet.Where(filter)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.Product)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.Customer)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.CustomerOrderSlip)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.DeliveryReceipt)
                .ThenInclude(dr => dr!.PurchaseOrder)
                .Include(c => c.ServiceInvoice)
                .ThenInclude(sv => sv!.Customer)
                .Include(c => c.ServiceInvoice)
                .ThenInclude(sv => sv!.Service)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public override async Task<IEnumerable<FilprideCreditMemo>> GetAllAsync(Expression<Func<FilprideCreditMemo, bool>>? filter, CancellationToken cancellationToken = default)
        {
            IQueryable<FilprideCreditMemo> query = dbSet
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.Product)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.Customer)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.CustomerOrderSlip)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.DeliveryReceipt)
                .ThenInclude(dr => dr!.PurchaseOrder)
                .Include(c => c.ServiceInvoice)
                .ThenInclude(sv => sv!.Customer)
                .Include(c => c.ServiceInvoice)
                .ThenInclude(sv => sv!.Service);

            if (filter != null)
            {
                query = query.Where(filter);
            }

            return await query.ToListAsync(cancellationToken);
        }

        public override IQueryable<FilprideCreditMemo> GetAllQuery(Expression<Func<FilprideCreditMemo, bool>>? filter = null)
        {
            IQueryable<FilprideCreditMemo> query = dbSet
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.Product)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.Customer)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.CustomerOrderSlip)
                .Include(c => c.SalesInvoice)
                .ThenInclude(s => s!.DeliveryReceipt)
                .Include(c => c.ServiceInvoice)
                .ThenInclude(sv => sv!.Customer)
                .Include(c => c.ServiceInvoice)
                .ThenInclude(sv => sv!.Service)
                .AsSplitQuery()
                .AsNoTracking();

            if (filter != null)
            {
                query = query.Where(filter);
            }

            return query;
        }
    }
}
