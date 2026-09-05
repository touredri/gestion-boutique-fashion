namespace BoutiqueFashion.Domain;

public enum UserRole { Seller, Manager, Administrator, ReadOnly }
public enum ProductType { Clothing, Shoes, Accessories }
public enum DiscountKind { None, Amount, Percentage }
public enum PaymentMode { Cash, OrangeMoney, MoovMoney, Wave, Card, BankTransfer, Credit, Custom }
public enum StockMovementType { Receipt, Sale, Return, Damaged, Lost, Adjustment, Inventory, Reversal, Reservation, ReservationRelease }
/// <summary><c>Reserved</c> : avance en cours, marchandise mise de côté et pas encore remise.
/// Elle devient <c>Completed</c> au solde, moment où le stock sort réellement.</summary>
public enum SaleStatus { Completed, Cancelled, Returned, Reserved }
public enum CreditStatus { Due, PartiallyPaid, Paid, Overdue, Disputed, Cancelled }
public enum CashSessionStatus { Open, Closed }
/// <summary><c>In</c> : apport de fond ou retour de monnaie. <c>Out</c> : prélèvement de recette,
/// dépôt en banque, sortie pour faire de la monnaie.</summary>
public enum CashMovementDirection { In, Out }
public enum DocumentType { Receipt, Invoice, Proforma, PaymentReceipt, DepositReceipt, CreditPaymentReceipt, BalanceReceipt, CreditNote, ReturnNote }
public enum CustomerSegment { New, Active, Loyal, Vip, Inactive, Debtor }
public enum PrintJobStatus { Pending, Printing, Completed, Failed }
public enum PrinterConnectionKind { WindowsQueue, SerialPort, TcpIp }
public enum PaperWidth { Mm58 = 58, Mm80 = 80 }
public enum DocumentStyle { Classique, Moderne, Minimal }
public enum PurchaseOrderStatus { Open, Closed }

/// <summary>D'où vient la commande. Le site vitrine n'est qu'un canal parmi d'autres : une
/// cliente qui écrit sur WhatsApp passe la même commande.</summary>
public enum OrderChannel { Vitrine, WhatsApp, Telephone }

/// <summary><c>Processed</c> signifie qu'une vente a été créée pour cette commande — c'est la
/// vente qui fait foi, jamais un simple changement d'état.</summary>
public enum OrderStatus { Pending, Processed, Delivered, Cancelled }

