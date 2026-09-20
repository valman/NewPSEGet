using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Pseget.Models;

namespace Pseget
{
    public interface IPseReportParser
    {
        PseDocumentModel Parse(string pdfText, DateOnly tradeDate);
    }
    
    public class PseReportParser : IPseReportParser
    {
        public PseDocumentModel Parse(string pdfText, DateOnly tradeDate)
        {
            var stocks = GetStocks(pdfText).ToList();
            var indexes = GetIndexes(pdfText)
                .ToArray();
            CalculateIndexNfb(indexes, pdfText);
            stocks.AddRange(indexes);
            return new PseDocumentModel
            {
                Stocks = stocks,
                TradeDate = tradeDate
            };
        }

        private IEnumerable<StockModel> GetStocks(string pdfText)
        {
            // match groups
            // group 0: matched line, ASIA UNITED AUB 46 46.9 46.75 46.75 46.05 46.05 6,000 277,200 -            
            // group 1: symbol
            const string pattern = @"(\b[A-Z0-9]+\b)\s+((((\(?\d{1,3}(,\d{3})*(\.\d+)?\)?))|-)\s|\n){9}(-?)";
            var matches = new Regex(pattern).Matches(pdfText);
            if (matches.Count == 0) return null;

            var result = new List<StockModel>();
            foreach (var match in matches.AsEnumerable())
            {
                var line = match.Groups[0].Value.Trim();
                var stockSymbol = match.Groups[1].Value.Trim();
                var numbers = line
                    .Split(' ')
                    .Reverse()
                    .Take(9)
                    .Select(x => x.Trim())
                    .Select(x =>
                    {
                        if (x.Contains("\n"))
                        {
                            return x.Split("\n")[1];
                        }
                        return x;
                    })
                    .ToArray();

                // skip stocks that did not open
                if (numbers[6] == "-") continue;

                Log.Debug(line);

                var stock = new StockModel
                {
                    Description = GetStockName(stockSymbol, pdfText),
                    Symbol = stockSymbol,
                    NetForeignBuy = decimal.TryParse(numbers[0], NumberStyles.Any, null, out var amount) ? amount : 0,
                    Value = decimal.Parse(numbers[1], NumberStyles.Any),
                    Volume = ulong.Parse(numbers[2], NumberStyles.Any),
                    Close = decimal.Parse(numbers[3], NumberStyles.Any),
                    Low = decimal.Parse(numbers[4], NumberStyles.Any),
                    High = decimal.Parse(numbers[5], NumberStyles.Any),
                    Open = decimal.Parse(numbers[6], NumberStyles.Any),
                };
                result.Add(stock);
            }
            return result;
        }

        private static IEnumerable<StockModel> GetIndexes(string pdfText)
        {
            // In recent reports, PSE renamed some labels: "Industrials" -> "Industrial", "PSEI" -> "PSEi"
            // Accept both spellings so old and new reports both parse.
            // Edited by ValMan
            const string pattern = @"(Financials|Industrials|Industrial|Holding Firms|Property|Services|Mining\s*&\s*Oil|PSEI|PSEi|All Shares)\s+(((((\(?\d{1,3}(,\d{3})*(\.\d+)?\)?))|-)\s|\n){8}|((((\(?\d{1,3}(,\d{3})*(\.\d+)?\)?))|-)\s|\n){6})";
            var matches = Regex.Matches(pdfText, pattern);
            if (matches.Count != 8)
            {
                var found = string.Join(", ", matches.Select(m => m.Groups[1].Value.Trim()));
                throw new Exception(
                    $"Unable to parse index values. Expected 8 sectoral summary rows but found {matches.Count}: [{found}]. " +
                    "The report layout or index labels may have changed.");
            }

            var result = new List<StockModel>();
            foreach (var match in matches.AsEnumerable())
            {
                var line = match.Groups[0].Value.Trim();
                var description = match.Groups[1].Value.Trim();
                var numbers = line
                        .Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries)
                        .Reverse()
                        .ToArray();
                var netForeign = 0m;

                // Edited by ValMan
                // Because PSE changed PSEI to PSEi...
                var isPsei = description.Equals("PSEi", StringComparison.OrdinalIgnoreCase);
                if (isPsei || description == "All Shares")
                {
                    var temp = numbers
                        .Take(6)
                        .Select(x => x.Trim())
                        .ToList();

                    if (isPsei)
                    {
                        // Edited by ValMan
                        // Read the two numbers that follow the GRAND TOTAL label, regardless of
                        // how much whitespace the PDF text extractor puts between them.
                        var totals = GetNumbersAfterLabel(pdfText, @"GRAND\s*TOTAL", 2);
                        if (totals == null)
                        {
                            throw new Exception(
                                "Unable to find GRAND TOTAL. Expected a volume and a value immediately after " +
                                "the 'GRAND TOTAL' label in the sectoral summary.");
                        }
                        temp.Insert(0, totals[1]); // psei value
                        temp.Insert(1, totals[0]); // psei volume
                        netForeign = GetNetForeign(pdfText);
                    }
                    else
                    {
                        temp.Insert(0, "0.0");
                        temp.Insert(1, "0.0");
                    }
                    numbers = temp.ToArray();
                }
                else
                {
                    numbers = numbers
                        .Take(9)
                        .Select(x => x.Trim())
                        .ToArray();
                }
                
                Log.Debug(line);
                //decimal amount = 0;
                var stock = new StockModel
                {
                    Description = description,
                    Symbol = GetIndexSymbol(description),
                    NetForeignBuy = netForeign,
                    Value = decimal.Parse(numbers[0], NumberStyles.Any),
                    Volume = ulong.Parse(numbers[1], NumberStyles.Any),
                    Close = decimal.Parse(numbers[4], NumberStyles.Any),
                    Low = decimal.Parse(numbers[5], NumberStyles.Any),
                    High = decimal.Parse(numbers[6], NumberStyles.Any),
                    Open = decimal.Parse(numbers[7], NumberStyles.Any),
                };
                result.Add(stock);
            }

            return result;
        }

        private static decimal GetNetForeign(string pdfText)
        {
            // Edited by ValMan
            // PSE changed the currency label changed from "Php" to "PHP"; match either, and tolerate
            // any amount of whitespace before the amount.
            var numbers = GetNumbersAfterLabel(pdfText, @"NET\s*FOREIGN\s*BUYING/\(SELLING\)\:\s*(?i:php)", 1);
            if (numbers == null) throw new Exception("Unable to find NFB");

            return decimal.Parse(numbers[0], NumberStyles.Any);
        }

        private static readonly char[] WhitespaceChars = { ' ', '\t', '\r', '\n', '\f', '\u00a0' };

        private static readonly Regex NumberTokenRegex =
            new Regex(@"^(\(?-?\d{1,3}(,\d{3})*(\.\d+)?\)?|-)$", RegexOptions.Compiled);

        /// <summary>
        /// Returns the first <paramref name="count"/> numeric tokens that immediately follow
        /// <paramref name="labelPattern"/>, ignoring how the extracted PDF text is spaced or
        /// wrapped. Returns null when the label is absent or is not followed by enough numbers.
        /// </summary>
        private static string[] GetNumbersAfterLabel(string pdfText, string labelPattern, int count)
        {
            foreach (var label in Regex.Matches(pdfText, labelPattern).AsEnumerable())
            {
                var tail = pdfText.Substring(label.Index + label.Length);
                if (tail.Length > 400) tail = tail.Substring(0, 400);

                var tokens = new List<string>();
                foreach (var token in tail.Split(WhitespaceChars, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!NumberTokenRegex.IsMatch(token)) break;
                    tokens.Add(token);
                    if (tokens.Count == count) return tokens.ToArray();
                }
            }

            return null;
        }

        private const string Financials = "^FINANCIALS";
        private const string Industrials = "^INDUSTRIAL";
        private const string Holding = "^HOLDING";
        private const string Property = "^PROPERTY";
        private const string Services = "^SERVICE";
        private const string Mining = "^MINING-OIL";
        private const string AllShares = "^ALLSHARES";
        private const string PSEi = "^PSEi";

        private static string GetIndexSymbol(string indexName)
        {
            return indexName switch
            {
                "Financials" => Financials,
                "Industrials" => Industrials,
                "Industrial" => Industrials,
                "Holding Firms" => Holding,
                "Property" => Property,
                "Services" => Services,
                "Mining & Oil" => Mining,
                "All Shares" => AllShares,
                "PSEI" => PSEi,
                "PSEi" => PSEi,
                _ => throw new InvalidOperationException($"{indexName} is unknown")
            };
        }

        private static string GetStockName(string stockSymbol, string pdfText)
        {
            var pattern = @"(.+)\s+(" + stockSymbol + @")\s+((((\(?\d{1,3}(,\d{3})*(\.\d+)?\)?))|-)\s|\n){9}";
            var match = Regex.Match(pdfText, pattern);
            return !match.Success ? string.Empty : match.Groups[1].Value;
        }

        private void CalculateIndexNfb(IEnumerable<StockModel> indexes, string pdfText)
        {            
            var pattern = @"F I N A N C I A L S((.|\n)+)FINANCIALS SECTOR TOTAL";
            var matchText = Regex.Match(pdfText, pattern).Value;
            var stockModels = indexes as StockModel[] ?? indexes.ToArray();
            
            var sector = stockModels.SingleOrDefault(index => index.Symbol == Financials);
            var stocksInSector = GetStocks(matchText);
            sector.NetForeignBuy = stocksInSector?
                .Sum(stock => stock.NetForeignBuy) ?? 0m;

            pattern = @"I N D U S T R I A L((.|\n)+)INDUSTRIAL SECTOR TOTAL";
            matchText = Regex.Match(pdfText, pattern).Value;
            sector = stockModels.SingleOrDefault(index => index.Symbol == Industrials);
            stocksInSector = GetStocks(matchText);
            sector.NetForeignBuy = stocksInSector?
                .Sum(stock => stock.NetForeignBuy) ?? 0m;

            pattern = @"H O L D I N G  F I R M S((.|\n)+)HOLDING FIRMS SECTOR TOTAL";
            matchText = Regex.Match(pdfText, pattern).Value;
            sector = stockModels.SingleOrDefault(index => index.Symbol == Holding);
            stocksInSector = GetStocks(matchText);
            sector.NetForeignBuy = stocksInSector?
                .Sum(stock => stock.NetForeignBuy) ?? 0m;

            pattern = @"P R O P E R T Y((.|\n)+)PROPERTY SECTOR TOTAL";
            matchText = Regex.Match(pdfText, pattern).Value;
            sector = stockModels.SingleOrDefault(index => index.Symbol == Property);
            stocksInSector = GetStocks(matchText);
            sector.NetForeignBuy = stocksInSector?
                .Sum(stock => stock.NetForeignBuy) ?? 0m;

            pattern = @"S E R V I C E S((.|\n)+)SERVICES SECTOR TOTAL";
            matchText = Regex.Match(pdfText, pattern).Value;
            sector = stockModels.SingleOrDefault(index => index.Symbol == Services);
            stocksInSector = GetStocks(matchText);
            sector.NetForeignBuy = stocksInSector?
                .Sum(stock => stock.NetForeignBuy) ?? 0m;

            pattern = @"M I N I N G  &  O I L((.|\n)+)MINING & OIL SECTOR TOTAL";
            matchText = Regex.Match(pdfText, pattern).Value;
            sector = stockModels.SingleOrDefault(index => index.Symbol == Mining);
            stocksInSector = GetStocks(matchText);
            sector.NetForeignBuy = stocksInSector?
                .Sum(stock => stock.NetForeignBuy) ?? 0m;
        }
    }
}
