using System;
using System.Collections.Generic;

namespace vizo_backend.Models;

public partial class Account
{
    public int AccountId { get; set; }

    public string AccountCode { get; set; } = null!;

    public string AccountName { get; set; } = null!;

    public int? ParentAccountId { get; set; }

    public int AccountTypeId { get; set; }

    public bool IsGroup { get; set; }

    public decimal OpeningBalance { get; set; }

    public string CurrencyCode { get; set; } = null!;

    public bool IsActive { get; set; }

    public virtual AccountType AccountType { get; set; } = null!;

    public virtual ICollection<BankReconciliation> BankReconciliations { get; set; } = new List<BankReconciliation>();

    public virtual ICollection<Expense> ExpenseExpenseAccounts { get; set; } = new List<Expense>();

    public virtual ICollection<Expense> ExpensePaidFromAccounts { get; set; } = new List<Expense>();

    public virtual ICollection<Account> InverseParentAccount { get; set; } = new List<Account>();

    public virtual ICollection<JournalEntryLine> JournalEntryLines { get; set; } = new List<JournalEntryLine>();

    public virtual Account? ParentAccount { get; set; }

    public virtual ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
}
