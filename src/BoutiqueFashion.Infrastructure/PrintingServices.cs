using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Printing;
using System.Globalization;
using System.IO.Ports;
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
        var result = new List<PrinterProfile>();
        foreach (string printer in PrinterSettings.InstalledPrinters) result.Add(new PrinterProfile(printer, PrinterConnectionKind.WindowsQueue, printer, PaperWidth.Mm80));
        result.AddRange(SerialPort.GetPortNames().OrderBy(x => x).Select(x => new PrinterProfile($"Bluetooth / série {x}", PrinterConnectionKind.SerialPort, x, PaperWidth.Mm80)));
        return result;
    }

    public Task PrintTestAsync(PrinterProfile printer, CancellationToken cancellationToken = default) =>
        PrintBytesAsync(printer, BuildTest(printer), $"test:{printer.ConnectionKind}:{printer.Address}", cancellationToken);

    public Task PrintReceiptAsync(PrinterProfile printer, ReceiptData receipt, CancellationToken cancellationToken = default) =>
        PrintBytesAsync(printer, EscPosReceiptBuilder.Build(receipt, printer), $"receipt:{receipt.Number}:{receipt.IsDuplicate}", cancellationToken);

    private Task PrintBytesAsync(PrinterProfile printer, byte[] bytes, string key, CancellationToken cancellationToken) =>
        queue.EnqueueAsync(key, async token =>
        {
            if (printer.ConnectionKind == PrinterConnectionKind.WindowsQueue) RawPrinter.Send(printer.Address, bytes);
            else
            {
                using var port = new SerialPort(printer.Address, 9600, Parity.None, 8, StopBits.One) { WriteTimeout = 10_000 };
                port.Open(); await port.BaseStream.WriteAsync(bytes, token); await port.BaseStream.FlushAsync(token);
            }
        }, cancellationToken);

    private static byte[] BuildTest(PrinterProfile printer)
    {
        var data = new ReceiptData("BOUTIQUE FASHION", null, null, "TEST", DateTimeOffset.Now, null,
            [new ReceiptItem("Test d'impression réussi", 1, 0, 0, 0)], 0, 0, 0, [], "Imprimante prête");
        return EscPosReceiptBuilder.Build(data, printer);
    }
}

internal static class EscPosReceiptBuilder
{
    public static byte[] Build(ReceiptData receipt, PrinterProfile profile)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(858, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var width = profile.PaperWidth == PaperWidth.Mm58 ? 32 : 42;
        var bytes = new List<byte> { 0x1B, 0x40, 0x1B, 0x74, 0x13 };
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
        foreach (var payment in receipt.Payments) Add(bytes, encoding, Columns(payment.Mode.ToString(), $"{payment.AmountXof:N0}", width));
        if (receipt.ChangeXof > 0) Add(bytes, encoding, Columns("Monnaie rendue", $"{receipt.ChangeXof:N0}", width), true);
        Add(bytes, encoding, ""); Add(bytes, encoding, Center(receipt.Footer, width));
        bytes.AddRange([0x1B, 0x64, 0x04]);
        if (profile.CutPaper) bytes.AddRange([0x1D, 0x56, 0x41, 0x00]);
        return bytes.ToArray();
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
            if (StartDocPrinter(printer, 1, new DocInfo()) == 0 || !StartPagePrinter(printer) || !WritePrinter(printer, unmanaged, data.Length, out var written) || written != data.Length)
                throw new InvalidOperationException($"Échec de l'impression ({Marshal.GetLastWin32Error()}).");
            EndPagePrinter(printer); EndDocPrinter(printer);
        }
        finally { Marshal.FreeCoTaskMem(unmanaged); ClosePrinter(printer); }
    }
}

public sealed class A4DocumentService(AppPaths paths) : IA4DocumentService
{
    public byte[] CreateInvoicePdf(ReceiptData data)
    {
        var document = Build(data); var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument(); using var stream = new MemoryStream(); renderer.PdfDocument.Save(stream, false); return stream.ToArray();
    }

    public async Task PrintInvoiceAsync(ReceiptData data, string? printerName = null, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.Documents, $"{data.Number}.pdf"); await File.WriteAllBytesAsync(path, CreateInvoicePdf(data), cancellationToken);
        var info = new ProcessStartInfo(path) { UseShellExecute = true, Verb = string.IsNullOrWhiteSpace(printerName) ? "print" : "printto", Arguments = string.IsNullOrWhiteSpace(printerName) ? string.Empty : $"\"{printerName}\"" };
        Process.Start(info);
    }

    private static Document Build(ReceiptData data)
    {
        var doc = new Document(); var section = doc.AddSection(); section.PageSetup.PageFormat = PageFormat.A4; section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8); section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
        if(!string.IsNullOrWhiteSpace(data.LogoPath)&&File.Exists(data.LogoPath)){var image=section.AddImage(data.LogoPath);image.Width=Unit.FromCentimeter(3);image.LockAspectRatio=true;}
        var title = section.AddParagraph(data.ShopName); title.Format.Font.Size = 20; title.Format.Font.Bold = true; title.Format.Font.Color = Colors.DarkSlateGray;
        if(!string.IsNullOrWhiteSpace(data.Slogan))section.AddParagraph(data.Slogan).Format.Font.Italic=true;
        if (!string.IsNullOrWhiteSpace(data.Address)) section.AddParagraph(data.Address); if (!string.IsNullOrWhiteSpace(data.Phone)) section.AddParagraph(data.Phone);if(!string.IsNullOrWhiteSpace(data.Email))section.AddParagraph(data.Email);if(!string.IsNullOrWhiteSpace(data.TaxId))section.AddParagraph($"NIF / RCCM : {data.TaxId}");
        var heading = section.AddParagraph($"FACTURE {data.Number}"); heading.Format.SpaceBefore = Unit.FromCentimeter(1); heading.Format.Font.Size = 16; heading.Format.Font.Bold = true;
        section.AddParagraph($"Date : {data.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}"); if (!string.IsNullOrWhiteSpace(data.Customer)) section.AddParagraph($"Client : {data.Customer}"); if (data.IsDuplicate) { var duplicate = section.AddParagraph("DUPLICATA"); duplicate.Format.Font.Bold = true; duplicate.Format.Font.Color = Colors.Firebrick; }
        var table = section.AddTable(); table.Borders.Width = 0.5; table.AddColumn(Unit.FromCentimeter(8.5)); table.AddColumn(Unit.FromCentimeter(2)); table.AddColumn(Unit.FromCentimeter(3)); table.AddColumn(Unit.FromCentimeter(3));
        var header = table.AddRow(); header.Shading.Color = Colors.LightGray; header.Cells[0].AddParagraph("Article"); header.Cells[1].AddParagraph("Qté"); header.Cells[2].AddParagraph("Prix"); header.Cells[3].AddParagraph("Total");
        foreach (var item in data.Items) { var row = table.AddRow(); row.Cells[0].AddParagraph(item.Description); row.Cells[1].AddParagraph(item.Quantity.ToString("0.###")); row.Cells[2].AddParagraph($"{item.UnitPriceXof:N0}"); row.Cells[3].AddParagraph($"{item.TotalXof:N0}"); }
        var total = section.AddParagraph($"TOTAL : {data.TotalXof:N0} FCFA"); total.Format.Alignment = ParagraphAlignment.Right; total.Format.Font.Size = 15; total.Format.Font.Bold = true; total.Format.SpaceBefore = Unit.FromCentimeter(0.5);
        var amounts = section.AddParagraph($"Sous-total : {data.SubtotalXof:N0} FCFA   ·   Remise : -{data.DiscountXof:N0} FCFA"); amounts.Format.Alignment = ParagraphAlignment.Right; amounts.Format.Font.Size = 9;
        if (data.ChangeXof > 0) { var change = section.AddParagraph($"Monnaie rendue : {data.ChangeXof:N0} FCFA"); change.Format.Alignment = ParagraphAlignment.Right; change.Format.Font.Size = 9; change.Format.Font.Bold = true; }
        if(!string.IsNullOrWhiteSpace(data.ReturnPolicy))section.AddParagraph(data.ReturnPolicy).Format.Font.Size=8;section.AddParagraph(data.Footer).Format.SpaceBefore = Unit.FromCentimeter(1);var validation=section.AddTable();validation.AddColumn(Unit.FromCentimeter(8));validation.AddColumn(Unit.FromCentimeter(8));var vr=validation.AddRow();if(!string.IsNullOrWhiteSpace(data.StampPath)&&File.Exists(data.StampPath))vr.Cells[0].AddImage(data.StampPath).Width=Unit.FromCentimeter(3);if(!string.IsNullOrWhiteSpace(data.SignaturePath)&&File.Exists(data.SignaturePath))vr.Cells[1].AddImage(data.SignaturePath).Width=Unit.FromCentimeter(3); return doc;
    }
}
