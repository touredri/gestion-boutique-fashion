using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Ports;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BoutiqueFashion.Application;
using BoutiqueFashion.Domain;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using Drawing = System.Drawing;

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

public sealed class ThermalPrinterService(IPrintQueueService queue, IAppSettingsService settings) : IThermalPrinterService
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

    public IReadOnlyList<TicketLine> Preview(ReceiptData receipt, PaperWidth paperWidth = PaperWidth.Mm80) => EscPosTicketLayout.Build(receipt, paperWidth);

    public Task<string> DiagnoseAsync(PrinterProfile printer, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        switch (printer.ConnectionKind)
        {
            case PrinterConnectionKind.WindowsQueue:
                return RawPrinter.Describe(printer.Address);
            case PrinterConnectionKind.SerialPort:
                try
                {
                    using var serial = new SerialPort(printer.Address, 9600) { WriteTimeout = 2000 };
                    serial.Open(); serial.Close();
                    return $"Port série {printer.Address} ouvert avec succès (9600 bauds). Si rien ne s'imprime, essayez 19200 ou 115200 bauds (réglage « Vitesse série »).";
                }
                catch (Exception e) { return $"Port série {printer.Address} inaccessible : {e.Message}"; }
            default:
                return $"Imprimante réseau {printer.Address} : lancez « Imprimer un ticket test » pour vérifier la connexion.";
        }
    }, cancellationToken);

    public async Task PrintTestAsync(PrinterProfile printer, CancellationToken cancellationToken = default)
    {
        var profile = await ApplySettingsAsync(printer, cancellationToken);
        await PrintLinesAsync(profile, BuildTest(profile), $"test:{profile.ConnectionKind}:{profile.Address}", cancellationToken);
    }

    public async Task PrintReceiptAsync(PrinterProfile printer, ReceiptData receipt, CancellationToken cancellationToken = default)
    {
        var profile = await ApplySettingsAsync(printer, cancellationToken);
        await PrintLinesAsync(profile, receipt, $"receipt:{receipt.Number}:{receipt.IsDuplicate}", cancellationToken);
    }

    private async Task<PrinterProfile> ApplySettingsAsync(PrinterProfile printer, CancellationToken cancellationToken) =>
        printer with
        {
            CutPaper = (await settings.GetAsync("Printer.CutPaper", cancellationToken) ?? "1") == "1",
            PaperWidth = (await settings.GetAsync("Printer.PaperWidth", cancellationToken) ?? "80") == "58" ? PaperWidth.Mm58 : PaperWidth.Mm80
        };

    private Task PrintLinesAsync(PrinterProfile printer, ReceiptData receipt, string key, CancellationToken cancellationToken = default) =>
        queue.EnqueueAsync(key, async token =>
        {
            if (printer.ConnectionKind == PrinterConnectionKind.WindowsQueue && (await settings.GetAsync("Printer.RenderMode", token) ?? "Raw") == "Gdi")
            {
                var lines = EscPosTicketLayout.Build(receipt, printer.PaperWidth);
                await Task.Run(() => GdiTicketRenderer.Print(printer.Address, lines), token);
                return;
            }
            var bytes = EscPosReceiptBuilder.Build(receipt, printer);
            switch (printer.ConnectionKind)
            {
                case PrinterConnectionKind.WindowsQueue:
                    await Task.Run(() => RawPrinter.Send(printer.Address, bytes), token);
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
                    var baud = int.TryParse(await settings.GetAsync("Printer.SerialBaud", token), out var parsedBaud) ? parsedBaud : 9600;
                    using var serial = new SerialPort(printer.Address, baud, Parity.None, 8, StopBits.One) { WriteTimeout = 10_000 };
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

internal static class EscPosTicketLayout
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static int Columns(PaperWidth paper) => paper == PaperWidth.Mm58 ? 32 : 42;

    public static IReadOnlyList<TicketLine> Build(ReceiptData receipt, PaperWidth paper = PaperWidth.Mm80)
    {
        var width = Columns(paper);
        return receipt.Style switch
        {
            DocumentStyle.Classique => BuildClassique(receipt, width),
            DocumentStyle.Minimal => BuildMinimal(receipt, width),
            _ => BuildModerne(receipt, width)
        };
    }

    private static List<TicketLine> BuildClassique(ReceiptData receipt, int width)
    {
        var lines = new List<TicketLine> { new(Center(receipt.ShopName.ToUpperInvariant(), width), true, true) };
        if (!string.IsNullOrWhiteSpace(receipt.Address)) lines.Add(new TicketLine(Center(receipt.Address, width)));
        if (!string.IsNullOrWhiteSpace(receipt.Phone)) lines.Add(new TicketLine(Center(receipt.Phone, width)));
        lines.Add(new TicketLine(new string('-', width)));
        lines.Add(new TicketLine(Fit($"{Libelles.Text(receipt.Kind)} : {receipt.Number}", width)));
        lines.Add(new TicketLine(receipt.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)));
        if (!string.IsNullOrWhiteSpace(receipt.Customer)) lines.Add(new TicketLine(Fit($"Client: {receipt.Customer}", width)));
        if (receipt.IsDuplicate) lines.Add(new TicketLine(Center("*** DUPLICATA ***", width), true));
        lines.Add(new TicketLine(new string('-', width)));
        foreach (var item in receipt.Items)
        {
            lines.Add(new TicketLine(Fit(item.Description, width)));
            lines.Add(new TicketLine(Columns($"{item.Quantity:0.###} x {item.UnitPriceXof:N0}", $"{item.TotalXof:N0}", width)));
            if (item.DiscountXof > 0) lines.Add(new TicketLine(Columns("Remise", $"-{item.DiscountXof:N0}", width)));
        }
        lines.Add(new TicketLine(new string('-', width)));
        lines.Add(new TicketLine(Columns("Sous-total", $"{receipt.SubtotalXof:N0}", width)));
        if (receipt.DiscountXof > 0) lines.Add(new TicketLine(Columns("Remise", $"-{receipt.DiscountXof:N0}", width)));
        lines.Add(new TicketLine(Columns("TOTAL", $"{receipt.TotalXof:N0} FCFA", width), true));
        foreach (var payment in receipt.Payments) lines.Add(new TicketLine(Columns(Libelles.Text(payment.Mode), $"{payment.AmountXof:N0}", width)));
        if (receipt.ChangeXof > 0) lines.Add(new TicketLine(Columns("Monnaie rendue", $"{receipt.ChangeXof:N0}", width), true));
        lines.Add(new TicketLine(string.Empty));
        lines.Add(new TicketLine(Center(Fit(receipt.Footer, width), width)));
        return lines;
    }

    private static List<TicketLine> BuildModerne(ReceiptData receipt, int width)
    {
        var lines = new List<TicketLine> { new(Center(receipt.ShopName.ToUpperInvariant(), width), true, true) };
        if (!string.IsNullOrWhiteSpace(receipt.Slogan)) lines.Add(new TicketLine(Center(Fit($"* {receipt.Slogan} *", width), width)));
        if (!string.IsNullOrWhiteSpace(receipt.Address)) lines.Add(new TicketLine(Center(Fit(receipt.Address, width), width)));
        if (!string.IsNullOrWhiteSpace(receipt.Phone)) lines.Add(new TicketLine(Center(Fit($"Tel. {receipt.Phone}", width), width)));
        lines.Add(new TicketLine(new string('=', width)));
        lines.Add(new TicketLine(Columns(receipt.Number, receipt.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr), width)));
        if (receipt.Kind != DocumentType.Receipt) lines.Add(new TicketLine(Center(Libelles.Text(receipt.Kind).ToUpperInvariant(), width), true));
        if (!string.IsNullOrWhiteSpace(receipt.Customer)) lines.Add(new TicketLine(Fit($"Client : {receipt.Customer}", width), true));
        if (receipt.IsDuplicate) lines.Add(new TicketLine(Center("*** DUPLICATA ***", width), true));
        lines.Add(new TicketLine(new string('=', width)));
        foreach (var item in receipt.Items)
        {
            lines.Add(new TicketLine(Fit(item.Description, width)));
            lines.Add(new TicketLine(Columns($"  {item.Quantity:0.###} x {item.UnitPriceXof:N0}", $"{item.TotalXof:N0}", width)));
            if (item.DiscountXof > 0) lines.Add(new TicketLine(Columns("  dont remise", $"-{item.DiscountXof:N0}", width)));
        }
        lines.Add(new TicketLine(new string('=', width)));
        if (receipt.DiscountXof > 0) lines.Add(new TicketLine(Columns("Sous-total", $"{receipt.SubtotalXof:N0}", width)));
        if (receipt.DiscountXof > 0) lines.Add(new TicketLine(Columns("Remise globale", $"-{receipt.DiscountXof:N0}", width)));
        lines.Add(new TicketLine(new string('-', width)));
        lines.Add(new TicketLine(Columns("TOTAL", $"{receipt.TotalXof:N0} FCFA", width), true));
        lines.Add(new TicketLine(new string('-', width)));
        foreach (var payment in receipt.Payments) lines.Add(new TicketLine(Columns(Libelles.Text(payment.Mode), $"{payment.AmountXof:N0}", width)));
        if (receipt.ChangeXof > 0) lines.Add(new TicketLine(Columns("Monnaie rendue", $"{receipt.ChangeXof:N0}", width), true));
        lines.Add(new TicketLine(string.Empty));
        if (!string.IsNullOrWhiteSpace(receipt.ReturnPolicy)) lines.Add(new TicketLine(Center(Fit(receipt.ReturnPolicy, width), width)));
        lines.Add(new TicketLine(Center(Fit(receipt.Footer, width), width), true));
        return lines;
    }

    private static List<TicketLine> BuildMinimal(ReceiptData receipt, int width)
    {
        var lines = new List<TicketLine> { new(Fit(receipt.ShopName, width), true) };
        if (!string.IsNullOrWhiteSpace(receipt.Address)) lines.Add(new TicketLine(Fit(receipt.Address, width)));
        if (!string.IsNullOrWhiteSpace(receipt.Phone)) lines.Add(new TicketLine(Fit(receipt.Phone, width)));
        lines.Add(new TicketLine(string.Empty));
        lines.Add(new TicketLine(Fit($"{receipt.Number}  {receipt.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}", width)));
        if (receipt.Kind != DocumentType.Receipt) lines.Add(new TicketLine(Fit(Libelles.Text(receipt.Kind).ToUpperInvariant(), width), true));
        if (!string.IsNullOrWhiteSpace(receipt.Customer)) lines.Add(new TicketLine(Fit(receipt.Customer, width)));
        if (receipt.IsDuplicate) lines.Add(new TicketLine("DUPLICATA", true));
        lines.Add(new TicketLine(string.Empty));
        foreach (var item in receipt.Items)
        {
            lines.Add(new TicketLine(Fit(item.Description, width)));
            lines.Add(new TicketLine(Columns($"{item.Quantity:0.###} x {item.UnitPriceXof:N0}", $"{item.TotalXof:N0}", width)));
        }
        lines.Add(new TicketLine(string.Empty));
        lines.Add(new TicketLine(Columns("TOTAL", $"{receipt.TotalXof:N0} FCFA", width), true));
        foreach (var payment in receipt.Payments) lines.Add(new TicketLine(Columns(Libelles.Text(payment.Mode), $"{payment.AmountXof:N0}", width)));
        if (receipt.ChangeXof > 0) lines.Add(new TicketLine(Columns("Monnaie", $"{receipt.ChangeXof:N0}", width)));
        lines.Add(new TicketLine(string.Empty));
        lines.Add(new TicketLine(Fit(receipt.Footer, width)));
        return lines;
    }

    private static string Center(string value, int width) => value.Length >= width ? Fit(value, width) : value.PadLeft((width + value.Length) / 2);
    private static string Fit(string value, int width) => value.Length <= Math.Max(0, width) ? value : value[..Math.Max(0, width)];
    private static string Columns(string left, string right, int width)
    {
        left = Fit(left, Math.Max(1, width - right.Length - 1));
        return left + new string(' ', Math.Max(1, width - left.Length - right.Length)) + right;
    }
}

internal static class EscPosReceiptBuilder
{
    public static byte[] Build(ReceiptData receipt, PrinterProfile profile)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding(858, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        var bytes = Init();
        foreach (var line in EscPosTicketLayout.Build(receipt, profile.PaperWidth)) Add(bytes, encoding, line.Text, line.Bold, line.DoubleHeight);
        return Finish(bytes, profile);
    }

    private static List<byte> Init() => new() { 0x1B, 0x40, 0x1B, 0x74, 0x13 };

    private static byte[] Finish(List<byte> bytes, PrinterProfile profile)
    {
        bytes.AddRange([0x1B, 0x64, 0x04]);
        if (profile.CutPaper) bytes.AddRange([0x1D, 0x56, 0x41, 0x00]);
        return bytes.ToArray();
    }

    private static void Add(List<byte> bytes, Encoding encoding, string value, bool bold, bool doubleHeight)
    {
        bytes.AddRange([0x1B, 0x45, bold ? (byte)1 : (byte)0]);
        bytes.AddRange([0x1D, 0x21, doubleHeight ? (byte)0x11 : (byte)0]);
        bytes.AddRange(encoding.GetBytes(value + "\n"));
    }
}

internal static class GdiTicketRenderer
{
    private const string FontName = "Courier New";
    private static readonly string Sample = new('M', 42);

    public static void Print(string printerName, IReadOnlyList<TicketLine> lines)
    {
        using var document = new System.Drawing.Printing.PrintDocument { DocumentName = "Boutique Fashion" };
        document.PrinterSettings.PrinterName = printerName;
        if (!document.PrinterSettings.IsValid) throw new InvalidOperationException($"Imprimante indisponible : {printerName}.");
        document.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(0, 0, 0, 0);
        foreach (System.Drawing.Printing.PaperSize size in document.PrinterSettings.PaperSizes)
        {
            if (size.Width > 0 && size.Width <= 400) { document.DefaultPageSettings.PaperSize = size; break; }
        }
        var index = 0;
        document.PrintPage += (sender, e) =>
        {
            var graphics = e.Graphics!;
            var width = e.MarginBounds.Width > 0 ? e.MarginBounds.Width : e.PageBounds.Width;
            var bottom = e.MarginBounds.Height > 0 ? e.MarginBounds.Bottom : e.PageBounds.Height;
            var baseSize = ChooseFontSize(graphics, width);
            var y = (float)(e.MarginBounds.Height > 0 ? e.MarginBounds.Top : 0);
            while (index < lines.Count)
            {
                var line = lines[index];
                using var font = new Drawing.Font(FontName, line.DoubleHeight ? baseSize * 1.7f : baseSize, line.Bold ? Drawing.FontStyle.Bold : Drawing.FontStyle.Regular);
                var height = font.GetHeight(graphics);
                if (index > 0 && y + height > bottom) break;
                graphics.DrawString(string.IsNullOrEmpty(line.Text) ? " " : line.Text, font, Drawing.Brushes.Black, 0, y);
                y += height;
                index++;
            }
            e.HasMorePages = index < lines.Count;
        };
        document.Print();
    }

    private static float ChooseFontSize(Drawing.Graphics graphics, int width)
    {
        for (var size = 12f; size >= 5f; size -= 0.5f)
        {
            using var font = new Drawing.Font(FontName, size);
            if (graphics.MeasureString(Sample, font).Width <= width) return size;
        }
        return 5f;
    }
}

internal static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private sealed class DocInfo { [MarshalAs(UnmanagedType.LPWStr)] public string DocName = "Boutique Fashion"; [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile; [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW"; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private sealed class PrinterInfo2
    {
        public string pServerName = "", pPrinterName = "", pShareName = "", pPortName = "", pDriverName = "", pComment = "", pLocation = "";
        public nint pDevMode;
        public string pSepFile = "", pPrintProcessor = "", pDatatype = "", pParameters = "";
        public nint pSecurityDescriptor;
        public uint Attributes, Priority, DefaultPriority, StartTime, UntilHere, Status, cJobs;
    }
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool OpenPrinter(string name, out nint handle, nint defaults);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(nint handle);
    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int StartDocPrinter(nint handle, int level, [In] DocInfo docInfo);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(nint handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool StartPagePrinter(nint handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndPagePrinter(nint handle);
    [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(nint handle, nint bytes, int count, out int written);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)] private static extern bool GetPrinter(nint handle, int level, nint buffer, int size, out int needed);

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

    public static string Describe(string printerName)
    {
        if (!OpenPrinter(printerName, out var printer, 0))
            return $"Impossible d'ouvrir la file « {printerName} » (erreur Windows {Marshal.GetLastWin32Error()}).\nCette imprimante a peut-être été supprimée ou renommée : relancez la détection en changeant d'imprimante puis en revenant sur celle-ci.";
        try
        {
            GetPrinter(printer, 2, 0, 0, out var needed);
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinter(printer, 2, buffer, needed, out _))
                    return $"Lecture des informations de la file impossible (erreur Windows {Marshal.GetLastWin32Error()}).";
                var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                if (info is null) return "Informations de la file illisibles.";
                var report = new StringBuilder();
                report.AppendLine($"File : {info.pPrinterName}");
                report.AppendLine($"Pilote : {info.pDriverName}");
                report.AppendLine($"Port : {info.pPortName}");
                report.AppendLine($"État : {DescribeStatus(info.Status)}");
                report.AppendLine($"Travaux en attente : {info.cJobs}");
                var driver = info.pDriverName ?? string.Empty;
                if (driver.Contains("Generic", StringComparison.OrdinalIgnoreCase) || driver.Contains("Text Only", StringComparison.OrdinalIgnoreCase) || driver.Contains("GDI", StringComparison.OrdinalIgnoreCase))
                    report.AppendLine("Conseil : pilote non ESC/POS détecté — passez le « Mode d'impression » sur « Rendu Windows » puis relancez le ticket test.");
                if ((info.Status & 0x80) != 0) report.AppendLine("Conseil : imprimante hors ligne — vérifiez l'alimentation et le câble, puis redémarrez le terminal.");
                if ((info.Status & 0x18) != 0) report.AppendLine("Conseil : problème papier (bourrage ou rouleau vide).");
                if ((info.Status & 0x100000) != 0) report.AppendLine("Conseil : intervention requise sur l'imprimante (capot ouvert ?).");
                if (info.cJobs > 0) report.AppendLine("Conseil : videz la file d'attente Windows (Paramètres → Imprimantes) avant de réessayer.");
                return report.ToString().TrimEnd();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { ClosePrinter(printer); }
    }

    private static string DescribeStatus(uint status)
    {
        if (status == 0) return "Prête";
        var labels = new List<string>();
        if ((status & 0x1) != 0) labels.Add("en pause");
        if ((status & 0x2) != 0) labels.Add("erreur");
        if ((status & 0x8) != 0) labels.Add("bourrage papier");
        if ((status & 0x10) != 0) labels.Add("papier épuisé");
        if ((status & 0x40) != 0) labels.Add("problème papier");
        if ((status & 0x80) != 0) labels.Add("hors ligne");
        if ((status & 0x400) != 0) labels.Add("impression en cours");
        if ((status & 0x1000) != 0) labels.Add("indisponible");
        if ((status & 0x400000) != 0) labels.Add("capot ouvert");
        return labels.Count == 0 ? $"code {status}" : string.Join(", ", labels);
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

    public byte[] CreatePreviewImage(ReceiptData data)
    {
        var renderer = new DocumentRenderer(Build(data));
        renderer.PrepareDocument();
        const int width = 595, height = 842;
        using var bitmap = new Drawing.Bitmap(width, height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.Clear(Drawing.Color.White);
            graphics.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAlias;
            using var xGraphics = XGraphics.FromGraphics(graphics, new XSize(width, height));
            renderer.RenderPage(xGraphics, 1);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, Drawing.Imaging.ImageFormat.Png);
        return stream.ToArray();
    }

    public async Task<string> PrintInvoiceAsync(ReceiptData data, string? printerName = null, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(paths.Documents, $"{data.Number}.pdf");
        await File.WriteAllBytesAsync(path, CreateInvoicePdf(data), cancellationToken);
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = string.IsNullOrWhiteSpace(printerName) ? "print" : "printto", Arguments = string.IsNullOrWhiteSpace(printerName) ? string.Empty : $"\"{printerName}\"" };
            System.Diagnostics.Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Impossible d'imprimer automatiquement le PDF (aucune application PDF associée ?). Copie enregistrée : {path}", e);
        }
        return path;
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

        var title = section.AddParagraph($"{Libelles.Text(data.Kind).ToUpperInvariant()} {data.Number}"); title.Format.SpaceBefore = Unit.FromCentimeter(0.7); title.Format.Font.Size = 16; title.Format.Font.Bold = true; title.Format.Font.Color = Ink;
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
        var title = section.AddParagraph($"{Libelles.Text(data.Kind).ToUpperInvariant()} {data.Number}"); title.Format.Font.Size = 12; title.Format.Font.Bold = true;
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
        var heading = section.AddParagraph($"{Libelles.Text(data.Kind).ToUpperInvariant()} {data.Number}"); heading.Format.SpaceBefore = Unit.FromCentimeter(1); heading.Format.Font.Size = 16; heading.Format.Font.Bold = true;
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

public static class PrinterStore
{
    public static async Task<PrinterProfile?> LoadAsync(IAppSettingsService settings, IThermalPrinterService printers, CancellationToken cancellationToken = default)
    {
        var saved = await settings.GetAsync("Printer.Selected", cancellationToken);
        var discovered = printers.Discover();
        if (!string.IsNullOrWhiteSpace(saved))
        {
            var profile = JsonSerializer.Deserialize<PrinterProfile>(saved);
            if (profile is not null && (profile.ConnectionKind == PrinterConnectionKind.TcpIp
                || discovered.Any(x => x.ConnectionKind == profile.ConnectionKind && string.Equals(x.Address, profile.Address, StringComparison.OrdinalIgnoreCase))))
                return profile;
        }
        return discovered.FirstOrDefault();
    }

    public static Task SaveAsync(IAppSettingsService settings, PrinterProfile profile, CancellationToken cancellationToken = default) =>
        settings.SetAsync("Printer.Selected", JsonSerializer.Serialize(profile), "Vendeur boutique", cancellationToken);
}
