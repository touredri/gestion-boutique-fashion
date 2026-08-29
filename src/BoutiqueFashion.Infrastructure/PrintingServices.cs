using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace BoutiqueFashion.Infrastructure;

public sealed class PrintQueueService : IPrintQueueService
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Task> pending = new(StringComparer.Ordinal);

    public Task EnqueueAsync(string idempotencyKey, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
        pending.GetOrAdd(idempotencyKey, _ => RunAsync(idempotencyKey, operation, cancellationToken));

    private async Task RunAsync(string key, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { await operation(cancellationToken); }
        finally { gate.Release(); pending.TryRemove(key, out _); }
    }
}

public sealed class ThermalPrinterService(IPrintQueueService queue) : IThermalPrinterService
{
    public IReadOnlyList<PrinterProfile> Discover()
    {
        string defaultPrinter;
        try { defaultPrinter = new System.Drawing.Printing.PrinterSettings().PrinterName; }
        catch { defaultPrinter = string.Empty; }
        var result = new List<PrinterProfile>();
        foreach (string printer in System.Drawing.Printing.PrinterSettings.InstalledPrinters)
        {
            var label = string.Equals(printer, defaultPrinter, StringComparison.OrdinalIgnoreCase) ? $"{printer} (par défaut)" : printer;
            result.Add(new PrinterProfile(label, PrinterConnectionKind.WindowsQueue, printer, PaperWidth.Mm80));
        }
        result = result
            .OrderByDescending(x => string.Equals(x.Address, defaultPrinter, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        result.AddRange(SerialPort.GetPortNames().OrderBy(x => x).Select(x => new PrinterProfile($"Bluetooth / série {x}", PrinterConnectionKind.SerialPort, x, PaperWidth.Mm80)));
        return result;
    }

    public Task PrintTestAsync(PrinterProfile printer, CancellationToken cancellationToken = default) =>
        PrintBytesAsync(printer, EscPosReceiptBuilder.Build(BuildTest(printer), printer), $"test:{printer.ConnectionKind}:{printer.Address}", cancellationToken);

    public Task PrintReceiptAsync(PrinterProfile printer, ReceiptData receipt, CancellationToken cancellationToken = default) =>
        PrintBytesAsync(printer, EscPosReceiptBuilder.Build(receipt, printer), $"receipt:{receipt.Number}:{receipt.IsDuplicate}", cancellationToken);

    private Task PrintBytesAsync(PrinterProfile printer, byte[] bytes, string key, CancellationToken cancellationToken = default) =>
        queue.EnqueueAsync(key, async token =>
        {
            switch (printer.ConnectionKind)
            {
                case PrinterConnectionKind.WindowsQueue:
                    RawPrinter.Send(printer.Address, bytes);
                    break;
                case PrinterConnectionKind.TcpIp:
                    var parts = printer.Address.Split(':');
                    var port = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) ? parsedPort : 9100;
                    using (var client = new TcpClient())
                    {
                        await client.ConnectAsync(parts[0], port, token);
                        await using var stream = client.GetStream();
                        await stream.WriteAsync(bytes, token);
                        await stream.FlushAsync(token);
                    }
                    break;
                default:
                {
                    using var serial = new SerialPort(printer.Address, 9600, Parity.None, 8, StopBits.One) { WriteTimeout = 10_000 };
                    serial.Open();
                    await serial.BaseStream.WriteAsync(bytes, token);
                    await serial.BaseStream.FlushAsync(token);
                    break;
                }
            }
        }, cancellationToken);

    private static ReceiptData BuildTest(PrinterProfile printer)
    {
        var data = new ReceiptData("BOUTIQUE FASHION", null, null, "TEST", DateTimeOffset.Now, null,
            [new ReceiptItem("Test d'impression réussi", 1, 0, 0, 0)], 0, 0, 0, [], "Imprimante prête");
        return data;
    }
}

internal static class EscPosReceiptBuilder
{
    public static byte[] Build(ReceiptData receipt, PrinterProfile profile) => receipt.Style switch
    {
        DocumentStyle.Classique => BuildClassique(receipt, profile),
        DocumentStyle.Minimal => BuildMinimal(receipt, profile),
        _ => BuildModerne(receipt, profile)
    };

    private static List<byte> Init() => new() { 0x1B, 0x40, 0x1B, 0x74, 0x13 };

    private static byte[] Finish(List<byte> bytes, PrinterProfile profile)
    {
        bytes.AddRange([0x1B, 0x64, 0x04]);
        if (profile.CutPaper) bytes.AddRange([0x1D, 0x56, 0x41, 0x00]);
        return bytes.ToArray();
    }

    private static byte[] BuildClassique(ReceiptData receipt, PrinterProfile profile)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(858, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var width = profile.PaperWidth == PaperWidth.Mm58 ? 32 : 42;
        var bytes = Init();
        Add(bytes, encoding, Center(receipt.ShopName.ToUpperInvariant(), width), true, true);
        if (!string.IsNullOrWhiteSpace(receipt.Address)) Add(bytes, encoding, Center(receipt.Address, width));
        if (!string.IsNullOrWhiteSpace(receipt.Phone)) Add(bytes, encoding, Center(receipt.Phone, width));
        Add(bytes, encoding, new string('-', width));
        Add(bytes, encoding, $"Ticket: {receipt.Number}"); Add(bytes, encoding, receipt.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")));
        if (!string.IsNullOrWhiteSpace(receipt.Customer)) Add(bytes, encoding, $"Client: {receipt.Customer}");
        if (receipt.IsDuplicate) Add(bytes, encoding, Center("*** DUPLICATA ***", width), true);
        Add(bytes, encoding, new string('-', width));
        foreach (var item in receipt.Items)
        {
            Add(bytes, encoding, Fit(item.Description, width));
            Add(bytes, encoding, Columns($"{item.Quantity:0.###} x {item.UnitPriceXof:N0}", $"{item.TotalXof:N0}", width));
            if (item.DiscountXof > 0) Add(bytes, encoding, Columns("Remise", $"-{item.DiscountXof:N0}", width));
        }
        Add(bytes, encoding, new string('-', width));
        Add(bytes, encoding, Columns("Sous-total", $"{receipt.SubtotalXof:N0}", width));
        if (receipt.DiscountXof > 0) Add(bytes, encoding, Columns("Remise", $"-{receipt.DiscountXof:N0}", width));
        Add(bytes, encoding, Columns("TOTAL", $"{receipt.TotalXof:N0} FCFA", width), true, true);
        foreach (var payment in receipt.Payments) Add(bytes, encoding, Columns(Libelles.Text(payment.Mode), $"{payment.AmountXof:N0}", width));
        if (receipt.ChangeXof > 0) Add(bytes, encoding, Columns("Monnaie rendue", $"{receipt.ChangeXof:N0}", width), true);
        Add(bytes, encoding, ""); Add(bytes, encoding, Center(receipt.Footer, width));
        return Finish(bytes, profile);
    }

    private static byte[] BuildModerne(ReceiptData receipt, PrinterProfile profile)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(858, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var width = profile.PaperWidth == PaperWidth.Mm58 ? 32 : 42;
        var bytes = Init();
        Add(bytes, encoding, Center(receipt.ShopName.ToUpperInvariant(), width), true, true);
        if (!string.IsNullOrWhiteSpace(receipt.Slogan)) Add(bytes, encoding, Center($"* {receipt.Slogan} *", width));
        if (!string.IsNullOrWhiteSpace(receipt.Address)) Add(bytes, encoding, Center(Fit(receipt.Address, width), width));
        if (!string.IsNullOrWhiteSpace(receipt.Phone)) Add(bytes, encoding, Center($"Tel. {receipt.Phone}", width));
        Add(bytes, encoding, new string('=', width));
        Add(bytes, encoding, Columns(receipt.Number, receipt.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")), width));
        if (!string.IsNullOrWhiteSpace(receipt.Customer)) Add(bytes, encoding, $"Client : {receipt.Customer}", true);
        if (receipt.IsDuplicate) Add(bytes, encoding, Center("*** DUPLICATA ***", width), true);
        Add(bytes, encoding, new string('=', width));
        foreach (var item in receipt.Items)
        {
            Add(bytes, encoding, Fit(item.Description, width));
            Add(bytes, encoding, Columns($"  {item.Quantity:0.###} x {item.UnitPriceXof:N0}", $"{item.TotalXof:N0}", width));
            if (item.DiscountXof > 0) Add(bytes, encoding, Columns("  dont remise", $"-{item.DiscountXof:N0}", width));
        }
        Add(bytes, encoding, new string('=', width));
        if (receipt.DiscountXof > 0) Add(bytes, encoding, Columns("Sous-total", $"{receipt.SubtotalXof:N0}", width));
        if (receipt.DiscountXof > 0) Add(bytes, encoding, Columns("Remise globale", $"-{receipt.DiscountXof:N0}", width));
        Add(bytes, encoding, new string('-', width));
        Add(bytes, encoding, Columns("TOTAL", $"{receipt.TotalXof:N0} FCFA", width), true, true);
        Add(bytes, encoding, new string('-', width));
        foreach (var payment in receipt.Payments) Add(bytes, encoding, Columns(Libelles.Text(payment.Mode), $"{payment.AmountXof:N0}", width));
        if (receipt.ChangeXof > 0) Add(bytes, encoding, Columns("Monnaie rendue", $"{receipt.ChangeXof:N0}", width), true);
        Add(bytes, encoding, "");
        if (!string.IsNullOrWhiteSpace(receipt.ReturnPolicy)) Add(bytes, encoding, Center(Fit(receipt.ReturnPolicy, width), width));
        Add(bytes, encoding, Center(receipt.Footer, width), true);
        return Finish(bytes, profile);
    }

    private static byte[] BuildMinimal(ReceiptData receipt, PrinterProfile profile)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(858, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var width = profile.PaperWidth == PaperWidth.Mm58 ? 32 : 42;
        var bytes = Init();
        Add(bytes, encoding, receipt.ShopName, true);
        if (!string.IsNullOrWhiteSpace(receipt.Address)) Add(bytes, encoding, Fit(receipt.Address, width));
        if (!string.IsNullOrWhiteSpace(receipt.Phone)) Add(bytes, encoding, receipt.Phone);
        Add(bytes, encoding, "");
        Add(bytes, encoding, $"{receipt.Number}  {receipt.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}");
        if (!string.IsNullOrWhiteSpace(receipt.Customer)) Add(bytes, encoding, receipt.Customer);
        if (receipt.IsDuplicate) Add(bytes, encoding, "DUPLICATA", true);
        Add(bytes, encoding, "");
        foreach (var item in receipt.Items)
        {
            Add(bytes, encoding, Fit(item.Description, width));
            Add(bytes, encoding, Columns($"{item.Quantity:0.###} x {item.UnitPriceXof:N0}", $"{item.TotalXof:N0}", width));
        }
        Add(bytes, encoding, "");
        Add(bytes, encoding, Columns("TOTAL", $"{receipt.TotalXof:N0} FCFA", width), true);
        foreach (var payment in receipt.Payments) Add(bytes, encoding, Columns(Libelles.Text(payment.Mode), $"{payment.AmountXof:N0}", width));
        if (receipt.ChangeXof > 0) Add(bytes, encoding, Columns("Monnaie", $"{receipt.ChangeXof:N0}", width));
        Add(bytes, encoding, "");
        Add(bytes, encoding, Fit(receipt.Footer, width));
        return Finish(bytes, profile);
    }

    private static void Add(List<byte> bytes, Encoding encoding, string value, bool bold = false, bool doubleHeight = false)
    {
        bytes.AddRange([0x1B, 0x45, bold ? (byte)1 : (byte)0]);
        bytes.AddRange([0x1D, 0x21, doubleHeight ? (byte)0x11 : (byte)0]);
        bytes.AddRange(encoding.GetBytes(value + "\n"));
    }
    private static string Center(string value, int width) => value.Length >= width ? Fit(value, width) : value.PadLeft((width + value.Length) / 2);
    private static string Fit(string value, int width) => value.Length <= width ? value : value[..width];
    private static string Columns(string left, string right, int width) { left = Fit(left, Math.Max(1, width - right.Length - 1)); return left + new string(' ', Math.Max(1, width - left.Length - right.Length)) + right; }
}

internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private sealed class DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string DocName = "Boutique Fashion"; [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile; [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW"; }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool OpenPrinter(string name, out nint handle, nint defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(nint handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int StartDocPrinter(nint handle, int level, [In] DocInfo docInfo);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(nint handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(nint handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(nint handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(nint handle, nint bytes, int count, out int written);

    public static void Send(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var printer, 0)) throw new InvalidOperationException($"Imprimante indisponible ({Marshal.GetLastWin32Error()}).");
        var unmanaged = Marshal.AllocCoTaskMem(data.Length);
        try
        {
            Marshal.Copy(data, 0, unmanaged, data.Length);
            if (StartDocPrinter(printer, 1, new DocInfo()) == 0 || !StartPagePrinter(printer) || !WritePrinter(printer, unmanaged, data.Length, out _))
                throw new InvalidOperationException($"Échec de l'impression ({Marshal.GetLastWin32Error()}).");
            EndPagePrinter(printer); EndDocPrinter(printer);
        }
        finally { Marshal.FreeCoTaskMem(unmanaged); ClosePrinter(printer); }
    }
}

public sealed class A4DocumentService(AppPaths paths) : IA4DocumentService
{
    private static readonly Color Terracotta = new(168, 79, 53);
    private static readonly Color TerracottaSoft = new(246, 231, 225);
    private static readonly Color Ink = new(33, 30, 26);
    private static readonly Color Muted = new(109, 102, 95);

    public byte[] CreateInvoicePdf(ReceiptData data)
    {
        var document = Build(data); var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument(); using var stream = new MemoryStream(); renderer.PdfDocument.Save(stream, false); return stream.ToArray();
    }

    public async Task PrintInvoiceAsync(ReceiptData data, string? printerName = null, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.Documents, $"{data.Number}.pdf"); await File.WriteAllBytesAsync(path, CreateInvoicePdf(data), cancellationToken);
        var info = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = string.IsNullOrWhiteSpace(printerName) ? "print" : "printto", Arguments = string.IsNullOrWhiteSpace(printerName) ? string.Empty : $"\"{printerName}\"" };
        System.Diagnostics.Process.Start(info);
    }

    private static Document Build(ReceiptData data) => data.Style switch
    {
        DocumentStyle.Classique => BuildClassique(data),
        DocumentStyle.Minimal => BuildMinimal(data),
        _ => BuildModerne(data)
    };

    private static Document BuildModerne(ReceiptData data)
    {
        var doc = new Document(); var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8); section.PageSetup.RightMargin = Unit.FromCentimeter(1.8); section.PageSetup.TopMargin = Unit.FromCentimeter(1.4);

        var header = section.AddTable(); header.Borders.Width = 0; header.AddColumn(Unit.FromCentimeter(9.6)); header.AddColumn(Unit.FromCentimeter(8));
        var headerRow = header.AddRow();
        var left = headerRow.Cells[0];
        if (!string.IsNullOrWhiteSpace(data.LogoPath) && File.Exists(data.LogoPath)) { var logo = left.AddImage(data.LogoPath); logo.Width = Unit.FromCentimeter(2.6); logo.LockAspectRatio = true; }
        var name = left.AddParagraph(data.ShopName); name.Format.Font.Size = 20; name.Format.Font.Bold = true; name.Format.Font.Color = Terracotta;
        if (!string.IsNullOrWhiteSpace(data.Slogan)) { var slogan = left.AddParagraph(data.Slogan); slogan.Format.Font.Italic = true; slogan.Format.Font.Color = Muted; }
        var right = headerRow.Cells[1]; right.VerticalAlignment = VerticalAlignment.Bottom;
        foreach (var line in new[] { data.Address, data.Phone, data.Email, string.IsNullOrWhiteSpace(data.TaxId) ? null : $"NIF / RCCM : {data.TaxId}" })
            if (!string.IsNullOrWhiteSpace(line)) { var p = right.AddParagraph(line); p.Format.Alignment = ParagraphAlignment.Right; p.Format.Font.Size = 9; p.Format.Font.Color = Muted; }

        var rule = section.AddTable(); rule.Borders.Width = 0; rule.AddColumn(Unit.FromCentimeter(17.6));
        var ruleRow = rule.AddRow(); ruleRow.Height = Unit.FromCentimeter(0.12); ruleRow.Shading.Color = Terracotta;

        var title = section.AddParagraph($"FACTURE {data.Number}"); title.Format.SpaceBefore = Unit.FromCentimeter(0.7); title.Format.Font.Size = 16; title.Format.Font.Bold = true; title.Format.Font.Color = Ink;
        var date = section.AddParagraph($"Date : {data.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}"); date.Format.Font.Size = 10; date.Format.Font.Color = Muted;
        if (!string.IsNullOrWhiteSpace(data.Customer)) { var client = section.AddParagraph($"Client : {data.Customer}"); client.Format.Font.Size = 11; client.Format.Font.Bold = true; }
        if (data.IsDuplicate) { var dup = section.AddParagraph("DUPLICATA"); dup.Format.Font.Bold = true; dup.Format.Font.Color = Terracotta; }

        var table = section.AddTable(); table.Borders.Width = 0; table.TopPadding = Unit.FromCentimeter(0.12); table.BottomPadding = Unit.FromCentimeter(0.12);
        table.AddColumn(Unit.FromCentimeter(8.6)); table.AddColumn(Unit.FromCentimeter(2)); table.AddColumn(Unit.FromCentimeter(3.5)); table.AddColumn(Unit.FromCentimeter(3.5));
        var head = table.AddRow(); head.Shading.Color = Terracotta; head.Height = Unit.FromCentimeter(0.8);
        for (var i = 0; i < 4; i++) { var cell = head.Cells[i]; cell.VerticalAlignment = VerticalAlignment.Center; var p = cell.AddParagraph(new[] { "Article", "Qté", "Prix", "Total" }[i]); p.Format.Font.Bold = true; p.Format.Font.Color = Colors.White; p.Format.Font.Size = 10; if (i > 0) p.Format.Alignment = ParagraphAlignment.Right; }
        var alternate = false;
        foreach (var item in data.Items)
        {
            var row = table.AddRow();
            if (alternate) row.Shading.Color = TerracottaSoft;
            alternate = !alternate;
            row.Cells[0].AddParagraph(item.Description).Format.Font.Size = 10;
            row.Cells[1].AddParagraph(item.Quantity.ToString("0.###")).Format.Alignment = ParagraphAlignment.Right;
            row.Cells[2].AddParagraph($"{item.UnitPriceXof:N0}").Format.Alignment = ParagraphAlignment.Right;
            row.Cells[3].AddParagraph($"{item.TotalXof:N0}").Format.Alignment = ParagraphAlignment.Right;
        }

        var totals = section.AddTable(); totals.Borders.Width = 0; totals.AddColumn(Unit.FromCentimeter(10.6)); totals.AddColumn(Unit.FromCentimeter(7));
        var totalRow = totals.AddRow();
        var totalCell = totalRow.Cells[1];
        if (data.DiscountXof > 0) { var sub = totalCell.AddParagraph($"Sous-total : {data.SubtotalXof:N0} FCFA   ·   Remise : -{data.DiscountXof:N0} FCFA"); sub.Format.Alignment = ParagraphAlignment.Right; sub.Format.Font.Size = 9; sub.Format.Font.Color = Muted; }
        var total = totalCell.AddParagraph($"TOTAL : {data.TotalXof:N0} FCFA"); total.Format.Alignment = ParagraphAlignment.Right; total.Format.Font.Size = 15; total.Format.Font.Bold = true; total.Format.Font.Color = Terracotta;
        if (data.ChangeXof > 0) { var change = totalCell.AddParagraph($"Monnaie rendue : {data.ChangeXof:N0} FCFA"); change.Format.Alignment = ParagraphAlignment.Right; change.Format.Font.Size = 9; change.Format.Font.Bold = true; }

        if (!string.IsNullOrWhiteSpace(data.ReturnPolicy)) { var policy = section.AddParagraph(data.ReturnPolicy); policy.Format.Font.Size = 8; policy.Format.Font.Color = Muted; policy.Format.SpaceBefore = Unit.FromCentimeter(0.6); }
        var footer = section.AddParagraph(data.Footer); footer.Format.Font.Size = 10; footer.Format.Font.Color = Muted; footer.Format.Alignment = ParagraphAlignment.Center; footer.Format.SpaceBefore = Unit.FromCentimeter(0.4);

        var validation = section.AddTable(); validation.Borders.Width = 0; validation.AddColumn(Unit.FromCentimeter(9.6)); validation.AddColumn(Unit.FromCentimeter(8));
        var validationRow = validation.AddRow(); validationRow.Height = Unit.FromCentimeter(3);
        if (!string.IsNullOrWhiteSpace(data.StampPath) && File.Exists(data.StampPath)) validationRow.Cells[0].AddImage(data.StampPath).Width = Unit.FromCentimeter(3);
        if (!string.IsNullOrWhiteSpace(data.SignaturePath) && File.Exists(data.SignaturePath)) { var signature = validationRow.Cells[1].AddImage(data.SignaturePath); signature.Width = Unit.FromCentimeter(3); signature.Left = Unit.FromCentimeter(4); }
        return doc;
    }

    private static Document BuildMinimal(ReceiptData data)
    {
        var doc = new Document(); var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.2); section.PageSetup.RightMargin = Unit.FromCentimeter(2.2); section.PageSetup.TopMargin = Unit.FromCentimeter(2);

        var name = section.AddParagraph(data.ShopName); name.Format.Font.Size = 14; name.Format.Font.Bold = true;
        foreach (var line in new[] { data.Address, data.Phone, data.Email, string.IsNullOrWhiteSpace(data.TaxId) ? null : $"NIF / RCCM : {data.TaxId}" })
            if (!string.IsNullOrWhiteSpace(line)) { var p = section.AddParagraph(line); p.Format.Font.Size = 9; p.Format.Font.Color = Muted; }
        section.AddParagraph("");
        var title = section.AddParagraph($"FACTURE {data.Number}"); title.Format.Font.Size = 12; title.Format.Font.Bold = true;
        section.AddParagraph($"Date : {data.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}").Format.Font.Size = 10;
        if (!string.IsNullOrWhiteSpace(data.Customer)) section.AddParagraph($"Client : {data.Customer}").Format.Font.Size = 10;
        if (data.IsDuplicate) section.AddParagraph("DUPLICATA").Format.Font.Bold = true;

        var table = section.AddTable(); table.Borders.Width = 0.5; table.AddColumn(Unit.FromCentimeter(8.5)); table.AddColumn(Unit.FromCentimeter(2)); table.AddColumn(Unit.FromCentimeter(3)); table.AddColumn(Unit.FromCentimeter(3));
        var header = table.AddRow(); header.Cells[0].AddParagraph("Article").Format.Font.Bold = true; header.Cells[1].AddParagraph("Qté").Format.Font.Bold = true; header.Cells[2].AddParagraph("Prix").Format.Font.Bold = true; header.Cells[3].AddParagraph("Total").Format.Font.Bold = true;
        foreach (var item in data.Items) { var row = table.AddRow(); row.Cells[0].AddParagraph(item.Description); row.Cells[1].AddParagraph(item.Quantity.ToString("0.###")); row.Cells[2].AddParagraph($"{item.UnitPriceXof:N0}"); row.Cells[3].AddParagraph($"{item.TotalXof:N0}"); }

        var total = section.AddParagraph($"TOTAL : {data.TotalXof:N0} FCFA"); total.Format.Alignment = ParagraphAlignment.Right; total.Format.Font.Size = 13; total.Format.Font.Bold = true; total.Format.SpaceBefore = Unit.FromCentimeter(0.5);
        if (data.DiscountXof > 0) section.AddParagraph($"Sous-total : {data.SubtotalXof:N0} FCFA · Remise : -{data.DiscountXof:N0} FCFA").Format.Alignment = ParagraphAlignment.Right;
        if (data.ChangeXof > 0) section.AddParagraph($"Monnaie rendue : {data.ChangeXof:N0} FCFA").Format.Alignment = ParagraphAlignment.Right;
        var footer = section.AddParagraph(data.Footer); footer.Format.SpaceBefore = Unit.FromCentimeter(1); footer.Format.Font.Size = 9; footer.Format.Font.Color = Muted;
        var validation = section.AddTable(); validation.AddColumn(Unit.FromCentimeter(8)); validation.AddColumn(Unit.FromCentimeter(8)); var vr = validation.AddRow();
        if (!string.IsNullOrWhiteSpace(data.StampPath) && File.Exists(data.StampPath)) vr.Cells[0].AddImage(data.StampPath).Width = Unit.FromCentimeter(3);
        if (!string.IsNullOrWhiteSpace(data.SignaturePath) && File.Exists(data.SignaturePath)) vr.Cells[1].AddImage(data.SignaturePath).Width = Unit.FromCentimeter(3);
        return doc;
    }

    private static Document BuildClassique(ReceiptData data)
    {
        var doc = new Document(); var section = doc.AddSection(); section.PageSetup.PageFormat = PageFormat.A4; section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8); section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
        if (!string.IsNullOrWhiteSpace(data.LogoPath) && File.Exists(data.LogoPath)) { var image = section.AddImage(data.LogoPath); image.Width = Unit.FromCentimeter(3); image.LockAspectRatio = true; }
        var title = section.AddParagraph(data.ShopName); title.Format.Font.Size = 20; title.Format.Font.Bold = true; title.Format.Font.Color = Colors.DarkSlateGray;
        if (!string.IsNullOrWhiteSpace(data.Slogan)) section.AddParagraph(data.Slogan).Format.Font.Italic = true;
        if (!string.IsNullOrWhiteSpace(data.Address)) section.AddParagraph(data.Address); if (!string.IsNullOrWhiteSpace(data.Phone)) section.AddParagraph(data.Phone); if (!string.IsNullOrWhiteSpace(data.Email)) section.AddParagraph(data.Email); if (!string.IsNullOrWhiteSpace(data.TaxId)) section.AddParagraph($"NIF / RCCM : {data.TaxId}");
        var heading = section.AddParagraph($"FACTURE {data.Number}"); heading.Format.SpaceBefore = Unit.FromCentimeter(1); heading.Format.Font.Size = 16; heading.Format.Font.Bold = true;
        section.AddParagraph($"Date : {data.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}"); if (!string.IsNullOrWhiteSpace(data.Customer)) section.AddParagraph($"Client : {data.Customer}"); if (data.IsDuplicate) { var duplicate = section.AddParagraph("DUPLICATA"); duplicate.Format.Font.Bold = true; duplicate.Format.Font.Color = Colors.Firebrick; }
        var table = section.AddTable(); table.Borders.Width = 0.5; table.AddColumn(Unit.FromCentimeter(8.5)); table.AddColumn(Unit.FromCentimeter(2)); table.AddColumn(Unit.FromCentimeter(3)); table.AddColumn(Unit.FromCentimeter(3));
        var header = table.AddRow(); header.Shading.Color = Colors.LightGray; header.Cells[0].AddParagraph("Article"); header.Cells[1].AddParagraph("Qté"); header.Cells[2].AddParagraph("Prix"); header.Cells[3].AddParagraph("Total");
        foreach (var item in data.Items) { var row = table.AddRow(); row.Cells[0].AddParagraph(item.Description); row.Cells[1].AddParagraph(item.Quantity.ToString("0.###")); row.Cells[2].AddParagraph($"{item.UnitPriceXof:N0}"); row.Cells[3].AddParagraph($"{item.TotalXof:N0}"); }
        var total = section.AddParagraph($"TOTAL : {data.TotalXof:N0} FCFA"); total.Format.Alignment = ParagraphAlignment.Right; total.Format.Font.Size = 15; total.Format.Font.Bold = true; total.Format.SpaceBefore = Unit.FromCentimeter(0.5);
        var amounts = section.AddParagraph($"Sous-total : {data.SubtotalXof:N0} FCFA   ·   Remise : -{data.DiscountXof:N0} FCFA"); amounts.Format.Alignment = ParagraphAlignment.Right; amounts.Format.Font.Size = 9;
        if (data.ChangeXof > 0) { var change = section.AddParagraph($"Monnaie rendue : {data.ChangeXof:N0} FCFA"); change.Format.Alignment = ParagraphAlignment.Right; change.Format.Font.Size = 9; change.Format.Font.Bold = true; }
        if (!string.IsNullOrWhiteSpace(data.ReturnPolicy)) section.AddParagraph(data.ReturnPolicy).Format.Font.Size = 8;
        section.AddParagraph(data.Footer).Format.SpaceBefore = Unit.FromCentimeter(1);
        var validation = section.AddTable(); validation.AddColumn(Unit.FromCentimeter(8)); validation.AddColumn(Unit.FromCentimeter(8)); var vr = validation.AddRow();
        if (!string.IsNullOrWhiteSpace(data.StampPath) && File.Exists(data.StampPath)) vr.Cells[0].AddImage(data.StampPath).Width = Unit.FromCentimeter(3);
        if (!string.IsNullOrWhiteSpace(data.SignaturePath) && File.Exists(data.SignaturePath)) vr.Cells[1].AddImage(data.SignaturePath).Width = Unit.FromCentimeter(3);
        return doc;
    }
}
