namespace BoutiqueFashion.Domain;

public enum UserRole { Seller, Manager, Administrator, ReadOnly }
public enum ProductType { Clothing, Shoes, Accessories }
public enum DiscountKind { None, Amount, Percentage }
public enum PaymentMode { Cash, OrangeMoney, MoovMoney, Wave, Card, BankTransfer, Credit, Custom }
public enum StockMovementType { Receipt, Sale, Return, Damaged, Lost, Adjustment, Inventory, Reversal }
public enum SaleStatus { Completed, Cancelled, Returned }
public enum CreditStatus { Due, PartiallyPaid, Paid, Overdue, Disputed, Cancelled }
public enum CashSessionStatus { Open, Closed }
public enum DocumentType { Receipt, Invoice, Proforma, PaymentReceipt, DepositReceipt, CreditPaymentReceipt, BalanceReceipt, CreditNote, ReturnNote }
public enum CustomerSegment { New, Active, Loyal, Vip, Inactive, Debtor }
public enum PrintJobStatus { Pending, Printing, Completed, Failed }
public enum PrinterConnectionKind { WindowsQueue, SerialPort }
public enum PaperWidth { Mm58 = 58, Mm80 = 80 }

