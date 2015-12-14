﻿using System;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System.Xml.Serialization;
using System.Globalization;

namespace CAT.Model
{
    [Serializable]
    class SProp002 : Strategy
    {
        # region Fields
        private DateTime _clse;
        private int _nStocksTrade;
        private List<Candle> _candles;
        private ConcurrentDictionary<string, Tick> _val;
        #endregion

        #region Constructor / Destructor
        public SProp002()
            : base()
        {
            this.hour = 10;
            _candles = new List<Candle>();
            _val = new ConcurrentDictionary<string, Tick>();
            this.TodayTrades = new ConcurrentDictionary<string, Trade>();
        }
        #endregion

        #region Public override functions
        public override void Run(Tick tick)
        {
            currentTime = tick.Time;

            StartOrReset();

            if (Filter(tick)) return;

            _val.AddOrUpdate(tick.Symbol, tick, (k, v) => v = tick);

            if (TodayTrades.ContainsKey(tick.Symbol))
            {
                //TrailingStop(tick, _todaytrades[tick.Symbol]);

                var action = TodayTrades[tick.Symbol].Update(_val[tick.Symbol])
                    ? 0 : -TodayTrades[tick.Symbol].Type;
                Send(TodayTrades[tick.Symbol], action);

                if (TodayTrades[tick.Symbol].IsTrading) return;

                TodayTrades.TryRemove(tick.Symbol, out trade);
                trades.Add(trade);
            }
            Indicator();
            type = TodayTrades.Count == 0 ? 0 : 1;
        }

        public override void StartOrReset()
        {
            if (_clse.Date == currentTime.Date) return;

            _nStocksTrade = string.IsNullOrWhiteSpace(setup.Parameters)
                ? 10 : int.Parse(setup.Parameters.Split(' ')[0]);

            type = 0;
            _val.Clear();
            TodayTrades.Clear();

            _clse = currentTime.Date.AddHours(9.5).Add(
                TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time").GetUtcOffset(eodTime) -
                TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time").GetUtcOffset(eodTime));

            if (setup.DayTradeDuration > 6) eodTime = GetBell(_clse, setup.DayTradeDuration);

            GetLastEOD(currentTime);

            if (eodTime.Date == DateTime.Today)
                OnStatusChanged(setup.SetupId.ToString("Trade ID000 @ ") +
                    _clse.ToShortTimeString() + " Bell: " + eodTime.ToShortTimeString());
        }
        public override IEnumerable<Tick> Datafilter(IEnumerable<Tick> ticks)
        {
            return ticks.Where(t => t.Time > DateTime.Today || t.Time >= this.setup.OfflineStartTime);
        }
        public override void WarmUp(List<Tick> ticks)
        {
            candles = new List<Candle>();
            if (ticks.Count == 0) return;

            var files = Directory.GetFiles(DataDir, "COTAHIST" + "*xml");

            foreach (var file in files)
            {
                var year = ticks.First().Time.Year;
                if (ticks.First().Time.Month == 1) year--;
                if (year > int.Parse(new FileInfo(file).Name.Substring(10, 4))) continue;

                try
                {
                    using (var tr = new StreamReader(file))
                        candles.AddRange((List<Candle>)(new XmlSerializer(typeof(List<Candle>))).Deserialize(tr));
                }
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                    throw;
                }
            }

            
            //_stdLookback = int.Parse(setup.TechAnalysis.Split(' ')[1]);
            candles = candles.OrderBy(c => c.OpenTime).ThenBy(c => c.Symbol).ToList();
        }
        public override void TradableAssets(bool isonline)
        {
            base.TradableAssets();
            this.Symbols.Clear();
            this.candles.Clear();

            if (!isonline) return;

            var max = 5;

            #region Get EOD data
            try
            {
                using (var client = new System.Net.WebClient())
                {
                    _candles = new List<Candle>();

                    for (var i = 1; i <= 8 && _candles.Count == 0; i++)
                    {
                        var lastday = DateTime.Today.AddDays(-i);
                        var url = "http://www.bmfbovespa.com.br/fechamento-pregao/BuscarUltimosPregoes.aspx?" +
                            "Tipo=MercadoVistaDetalhe&Nivel=0&Ancora=A0&idioma=pt-br&Data=" + lastday.ToString("MMdd");

                        OnStatusChanged(url);

                        var result = client.DownloadString(url);

                        var index = result.IndexOf("tbody");
                        if (index < 0) continue;

                        var count = result.IndexOf("tbody", index + 1) - index;
                        result = result.Substring(index, count);

                        index = 0;

                        while ((index = result.IndexOf("<tr", index + 1)) > 0)
                        {
                            count = result.IndexOf("/tr", index) - index;

                            var idx = 0;
                            var pass = -2;
                            var candle = new Candle() { Period = "D", OpenTime = lastday.Date };
                            var line = result.Substring(index, count);

                            while ((idx = line.IndexOf("\">", idx) + 2) > 1)
                            {
                                pass++;
                                var field = line.Substring(idx, line.IndexOf("<", idx) - idx);

                                if (pass == 0) candle.Symbol = field.Replace("#", "").Trim();
                                if (pass == 2) candle.Indicators = field.Trim();
                                if (pass == 3) candle.OpenValue = decimal.Parse(field);
                                if (pass == 4) candle.MinValue = decimal.Parse(field);
                                if (pass == 5) candle.MaxValue = decimal.Parse(field);
                                if (pass == 6) candle.AveValue = decimal.Parse(field);
                                if (pass == 7) candle.CloseValue = decimal.Parse(field);
                                if (pass == 7) candle.AdjClValue = decimal.Parse(field);
                                if (pass == 11) candle.Trades = decimal.Parse(field);
                                if (pass == 12) candle.Quantity = decimal.Parse(field) / 1000;
                            }
                            if (candle.MinValue < 3 || candle.Trades < 100) continue;
                            if (!candle.Indicators.Contains("ON") && !candle.Indicators.Contains("PN")) continue;

                            _candles.Add(candle);
                        }
                    }
                }
                if (_candles.Count == 0) return;

                var symbols = new List<string>();
                var candlex = new List<Candle>();
                _candles.ForEach(c =>
                {
                    if (!symbols.Contains(c.Symbol.Substring(0, 4)))
                        symbols.Add(c.Symbol.Substring(0, 4));
                });
                symbols.ForEach(s => 
                {
                    var candlez = _candles.FindAll(c => c.Symbol.Contains(s));
                    if (candlez.Count < 2)
                        _candles.RemoveAll(c => c.Symbol.Contains(s));
                    else
                    {
                        var i = candlez.First().Trades > candlez.Last().Trades ? 0 : 1;
                        candlex.Add(candlez[i]);
                    }
                });

                symbols.Clear();
                max = Math.Min(max, candlex.Count);
                candlex = candlex.OrderByDescending(c => c.Trades).ToList().GetRange(0, max);
                candlex.ForEach(c =>
                {
                    if (!symbols.Contains(c.Symbol.Substring(0, 4)))
                        symbols.Add(c.Symbol.Substring(0, 4));
                });
                _candles.RemoveAll(c => !symbols.Contains(c.Symbol.Substring(0, 4)));

                this.Symbols = _candles.Select(c => c.Symbol).ToList();
            }
            catch (Exception e) { OnStatusChanged("SProp002: " + e.Message); return; }
            #endregion

            _candles.OrderByDescending(c => c.Symbol).ToList()
                .ForEach(c => { OnStatusChanged(c.ToString().Replace("D;", max.ToString("000") + ";").Replace(";", "\t")); max--; });

            if (currentTime > _clse) _candles.Clear();
        }
        public override bool Filter(Tick tick)
        {
            return !Symbols.Contains(tick.Symbol);
        }

        #endregion

        #region Private functions
        private bool Indicator()
        {
            var exitcode = true;
            if (_candles.Count == 0 || currentTime < _clse) return exitcode;

            foreach (var item in _val)
            {
                Candle candle;
                if ((candle = _candles.FirstOrDefault(c => c.Symbol == item.Key)) == null) continue;
                var type = candle.Symbol.Substring(4) != "3" ? -1 : 1;
                _candles.Remove(candle);
                
                var trade = new Trade(type, _val[candle.Symbol], setup);
                trade.CloseTime = eodTime;
                trade.IsTrading = true;
                TodayTrades.TryAdd(candle.Symbol, trade);
                Send(trade, 1);

                exitcode = false;
            }
            return exitcode;
        }
        private void TrailingStop(Tick tick, Trade trade)
        {
            if (!setup.DynamicLoss.HasValue) return;
            if (tick.Symbol != trade.Symbol) return;

            var tsl = trade.Type * (tick.Value - trade.EntryValue * (decimal?)setup.DynamicLoss * trade.Type);
            if (tsl > trade.Type * trade.StopLoss) trade.StopLoss = tsl * trade.Type;
        }
        private void GetLastEOD(DateTime today)
        {
            var max = 5;

            try
            {
                if (today.Date == DateTime.Today) { this.TradableAssets(true); return; }

                _candles = new List<Candle>();

                for (var i = 1; i <= 8 && _candles.Count == 0; i++)
                {
                    var tmpcandles = candles.FindAll(c => c.OpenTime.Date == today.AddDays(-i).Date);
                    if (tmpcandles.Count == 0) continue;
                    candles.RemoveRange(0, 1 + candles.FindIndex(c => c == tmpcandles.Last()));
                    tmpcandles.ForEach(c => _candles.Add(c));
                }
                if (_candles.Count == 0) return;

                var symbols = new List<string>();
                var candlex = new List<Candle>();
                _candles.ForEach(c =>
                {
                    if (!symbols.Contains(c.Symbol.Substring(0, 4)))
                        symbols.Add(c.Symbol.Substring(0, 4));
                });
                symbols.ForEach(s =>
                {
                    var candlez = _candles.FindAll(c => c.Symbol.Contains(s));
                    if (candlez.Count < 2)
                        _candles.RemoveAll(c => c.Symbol.Contains(s));
                    else
                    {
                        var i = candlez.First().Trades > candlez.Last().Trades ? 0 : 1;
                        candlex.Add(candlez[i]);
                    }
                });

                symbols.Clear();
                max = Math.Min(max, candlex.Count);
                candlex = candlex.OrderByDescending(c => c.Trades).ToList().GetRange(0, max);
                candlex.ForEach(c =>
                {
                    if (!symbols.Contains(c.Symbol.Substring(0, 4)))
                        symbols.Add(c.Symbol.Substring(0, 4));
                });
                _candles.RemoveAll(c => !symbols.Contains(c.Symbol.Substring(0, 4)));

                this.Symbols = _candles.Select(c => c.Symbol).ToList();
            }
            catch (Exception e) { OnStatusChanged("SProp002: " + e.Message); return; }
        }
        #endregion
    }
}