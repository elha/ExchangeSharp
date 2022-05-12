/*
MIT LICENSE

Copyright 2017 Digital Ruby, LLC - http://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExchangeSharp
{
	public sealed partial class ExchangeBitMEXAPI : ExchangeAPI
	{
		public override string BaseUrl { get; set; } = "https://www.bitmex.com/api/v1";
		public override string BaseUrlWebSocket { get; set; } = "wss://www.bitmex.com/realtime";
		//public override string BaseUrl { get; set; } = "https://testnet.bitmex.com/api/v1";
		//public override string BaseUrlWebSocket { get; set; } = "wss://testnet.bitmex.com/realtime";

		private SortedDictionary<long, decimal> dict_long_decimal = new SortedDictionary<long, decimal>();
		private SortedDictionary<decimal, long> dict_decimal_long = new SortedDictionary<decimal, long>();

		private ExchangeBitMEXAPI()
		{
			RequestWindow = TimeSpan.Zero;
			NonceStyle = NonceStyle.ExpiresUnixSeconds;

			// make the nonce go 60 seconds into the future (the offset is subtracted)
			// this will give us an api-expires 60 seconds into the future
			NonceOffset = TimeSpan.FromSeconds(-60.0);

			MarketSymbolSeparator = string.Empty;
			RequestContentType = "application/json";
			WebSocketOrderBookType = WebSocketOrderBookType.FullBookFirstThenDeltas;

			RateLimit = new RateGate(300, TimeSpan.FromMinutes(5));
			ExchangeGlobalCurrencyReplacements["XBT"] = "BTC";
		}

		public override Task<string> ExchangeMarketSymbolToGlobalMarketSymbolAsync(string marketSymbol)
		{
			throw new NotImplementedException();
		}

		public override Task<string> GlobalMarketSymbolToExchangeMarketSymbolAsync(string marketSymbol)
		{
			string m = marketSymbol;
			return Task.FromResult(m);
		}

		protected override async Task ProcessRequestAsync(IHttpWebRequest request, Dictionary<string, object> payload)
		{
			if (CanMakeAuthenticatedRequest(payload))
			{
				// convert nonce to long, trim off milliseconds
				var nonce = payload["nonce"].ConvertInvariant<long>();
				payload.Remove("nonce");
				var msg = CryptoUtility.GetJsonForPayload(payload);
				var sign = $"{request.Method}{request.RequestUri.AbsolutePath}{request.RequestUri.Query}{nonce}{msg}";
				string signature = CryptoUtility.SHA256Sign(sign, CryptoUtility.ToUnsecureBytesUTF8(PrivateApiKey));

				request.AddHeader("api-expires", nonce.ToStringInvariant());
				request.AddHeader("api-key", PublicApiKey.ToUnsecureString());
				request.AddHeader("api-signature", signature);

				await CryptoUtility.WritePayloadJsonToRequestAsync(request, payload);
			}
		}

		protected override async Task<IEnumerable<string>> OnGetMarketSymbolsAsync()
		{
			var m = await GetMarketSymbolsMetadataAsync();
			return m.Select(x => x.MarketSymbol);
		}


		protected internal override async Task<IEnumerable<ExchangeMarket>> OnGetMarketSymbolsMetadataAsync()
		{
			

			List<ExchangeMarket> markets = new List<ExchangeMarket>();
			var filter = HttpUtility.UrlEncode("{\"state\":\"Open\", \"typ\":\"FFWCSX\"}");
			JToken allSymbols = await MakeJsonRequestAsync<JToken>($"/instrument?count=500&filter={filter}&reverse=true");
			foreach (JToken marketSymbolToken in allSymbols)
			{
				var market = new ExchangeMarket
				{
					MarketSymbol = marketSymbolToken["symbol"].ToStringUpperInvariant(),
					IsActive = marketSymbolToken["state"].ToStringInvariant().EqualsWithOption("Open"),
					QuoteCurrency = marketSymbolToken["quoteCurrency"].ToStringUpperInvariant(),
					BaseCurrency = marketSymbolToken["underlying"].ToStringUpperInvariant(),
				};

				try
				{
					market.PriceStepSize = marketSymbolToken["tickSize"].ConvertInvariant<decimal>();
					market.MaxPrice = marketSymbolToken["maxPrice"].ConvertInvariant<decimal>();
					//market.MinPrice = symbol["minPrice"].ConvertInvariant<decimal>();

					market.MaxTradeSize = marketSymbolToken["maxOrderQty"].ConvertInvariant<decimal>();
					//market.MinTradeSize = symbol["minQty"].ConvertInvariant<decimal>();
					//market.QuantityStepSize = symbol["stepSize"].ConvertInvariant<decimal>();
				}
				catch
				{

				}
				markets.Add(market);
			}
			return markets;
		}

		protected override Task<IWebSocket> OnGetTradesWebSocketAsync(Func<KeyValuePair<string, ExchangeTrade>, Task> callback, params string[] marketSymbols)
		{
			/*
{"table":"trade","action":"partial","keys":[],
"types":{"timestamp":"timestamp","symbol":"symbol","side":"symbol","size":"long","price":"float","tickDirection":"symbol","trdMatchID":"guid","grossValue":"long","homeNotional":"float","foreignNotional":"float"},
"foreignKeys":{"symbol":"instrument","side":"side"},
"attributes":{"timestamp":"sorted","symbol":"grouped"},
"filter":{"symbol":"XBTUSD"},
"data":[{"timestamp":"2018-07-06T08:31:53.333Z","symbol":"XBTUSD","side":"Buy","size":10000,"price":6520,"tickDirection":"PlusTick","trdMatchID":"a296312f-c9a4-e066-2f9e-7f4cf2751f0a","grossValue":153370000,"homeNotional":1.5337,"foreignNotional":10000}]}
			 */

			return ConnectPublicWebSocketAsync(string.Empty, async (_socket, msg) =>
			{
				var str = msg.ToStringFromUTF8();
				JToken token = JToken.Parse(str);

				if (token["error"] != null)
				{
					Logger.Info(token["error"].ToStringInvariant());
					return;
				}
				else if (token["table"] == null)
				{
					return;
				}

				var action = token["action"].ToStringInvariant();
				JArray data = token["data"] as JArray;
				foreach (var t in data)
				{
					var marketSymbol = t["symbol"].ToStringInvariant();
					await callback(new KeyValuePair<string, ExchangeTrade>(marketSymbol, t.ParseTrade("size", "price", "side", "timestamp", TimestampType.Iso8601, "trdMatchID")));
				}
			}, async (_socket) =>
			{
				if (marketSymbols == null || marketSymbols.Length == 0)
				{
					await _socket.SendMessageAsync(new { op = "subscribe", args = "trade" });
				}
				else
				{
					await _socket.SendMessageAsync(new { op = "subscribe", args = marketSymbols.Select(s => "trade:" + this.NormalizeMarketSymbol(s)).ToArray() });
				}
			});
		}

		protected override async Task<IWebSocket> OnGetDeltaOrderBookWebSocketAsync(Action<ExchangeOrderBook> callback, int maxCount = 20, params string[] marketSymbols)
		{
			/*
{"info":"Welcome to the BitMEX Realtime API.","version":"2018-06-29T18:05:14.000Z","timestamp":"2018-07-05T14:22:26.267Z","docs":"https://www.bitmex.com/app/wsAPI","limit":{"remaining":39}}
{"success":true,"subscribe":"orderBookL2:XBTUSD","request":{"op":"subscribe","args":["orderBookL2:XBTUSD"]}}
{"table":"orderBookL2","action":"update","data":[{"symbol":"XBTUSD","id":8799343000,"side":"Buy","size":350544}]}
			 */

			if (marketSymbols == null || marketSymbols.Length == 0)
			{
				marketSymbols = (await GetMarketSymbolsAsync()).ToArray();
			}
			return await ConnectPublicWebSocketAsync(string.Empty, (_socket, msg) =>
			{
				var str = msg.ToStringFromUTF8();
				JToken token = JToken.Parse(str);

				if (token["table"] == null)
				{
					return Task.CompletedTask;
				}

				var action = token["action"].ToStringInvariant();
				JArray data = token["data"] as JArray;

				ExchangeOrderBook book = new ExchangeOrderBook();
				var price = 0m;
				var size = 0m;
				foreach (var d in data)
				{
					var marketSymbol = d["symbol"].ToStringInvariant();
					var id = d["id"].ConvertInvariant<long>();
					if (d["price"] == null)
					{
						if (!dict_long_decimal.TryGetValue(id, out price))
						{
							continue;
						}
					}
					else
					{
						price = d["price"].ConvertInvariant<decimal>();
						dict_long_decimal[id] = price;
						dict_decimal_long[price] = id;
					}

					var side = d["side"].ToStringInvariant();

					if (d["size"] == null)
					{
						size = 0m;
					}
					else
					{
						size = d["size"].ConvertInvariant<decimal>();
					}

					var depth = new ExchangeOrderPrice { Price = price, Amount = size };

					if (side.EqualsWithOption("Buy"))
					{
						book.Bids[depth.Price] = depth;
					}
					else
					{
						book.Asks[depth.Price] = depth;
					}
					book.MarketSymbol = marketSymbol;
				}

				if (!string.IsNullOrEmpty(book.MarketSymbol))
				{
					callback(book);
				}
				return Task.CompletedTask;
			}, async (_socket) =>
			{
				if (marketSymbols.Length == 0)
				{
					marketSymbols = (await GetMarketSymbolsAsync()).ToArray();
				}
				await _socket.SendMessageAsync(new { op = "subscribe", args = marketSymbols.Select(s => "orderBookL2:" + this.NormalizeMarketSymbol(s)).ToArray() });
			});
		}

		protected override async Task<IWebSocket> OnGetPositionsWebSocketAsync(Action<ExchangePosition> callback)
		{

			return await ConnectPublicWebSocketAsync(string.Empty, (_socket, msg) =>
			{
				var str = msg.ToStringFromUTF8();
				JToken token = JToken.Parse(str);
				if (token["error"] != null)
				{
					Logger.Info(token["error"].ToStringInvariant());
					return Task.CompletedTask;
				}
				else if (token["table"] == null)
				{
					return Task.CompletedTask;
				}

				JArray data = token["data"] as JArray;
				foreach (var d in data)
				{
					var position = ParsePosition(d);
					callback(position);
				}
				return Task.CompletedTask;

			}, async (_socket) =>
			{
				long nonce = (await GenerateNonceAsync()).ConvertInvariant<long>();
				var authPayload = $"GET/realtime{nonce}";
				string signature = CryptoUtility.SHA256Sign(authPayload, CryptoUtility.ToUnsecureBytesUTF8(PrivateApiKey));

				var authArgs = new object[]{PublicApiKey.ToUnsecureString(), nonce, signature};
				await _socket.SendMessageAsync(new { op = "authKeyExpires", args = authArgs });
				await _socket.SendMessageAsync(new { op = "subscribe", args = "position" });
			});
		}

		protected override async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> OnGetTickersAsync()
		{
			List<KeyValuePair<string, ExchangeTicker>> tickers = new List<KeyValuePair<string, ExchangeTicker>>();
			IReadOnlyDictionary<string, ExchangeMarket> marketsBySymbol = (await GetMarketSymbolsMetadataAsync()).ToDictionary(market => market.MarketSymbol, market => market);
			if (marketsBySymbol != null && marketsBySymbol.Count != 0)
			{
				StringBuilder symbolString = new StringBuilder();
				foreach (var marketSymbol in marketsBySymbol.Keys)
				{
					symbolString.Append(marketSymbol.ToUpperInvariant());
					symbolString.Append(',');
				}
				symbolString.Length--;
				var filter = HttpUtility.UrlEncode("{\"state\":\"Open\", \"typ\":\"FFWCSX\"}");
				JToken token = await MakeJsonRequestAsync<JToken>("/instrument?symbols=" + symbolString + "&filter=" + filter + "&columns=symbol,askPrice,bidPrice,lastPrice,volume,settlCurrency,lotSize,takerFee,midPrice,quoteToSettleMultiplier,multiplier");
				DateTime now = CryptoUtility.UtcNow;
				foreach (JToken array in token)
				{
					#region Return Values

					#endregion Return Values

					var marketSymbol = array["symbol"].ToStringInvariant();
					var market = marketsBySymbol[marketSymbol];
					tickers.Add(new KeyValuePair<string, ExchangeTicker>(marketSymbol, new ExchangeTicker
					{
						Exchange = Name,
						MarketSymbol = marketSymbol,
						ApiResponse = token,
						Ask = array["askPrice"].ConvertInvariant<decimal>(),
						Bid = array["bidPrice"].ConvertInvariant<decimal>(),
						Last = array["midPrice"].ConvertInvariant<decimal>(),
						SettleCurrency = array["settlCurrency"].ToStringInvariant().ToUpper(),
						LotSize = array["lotSize"].ConvertInvariant<decimal>(),
						Fee = array["takerFee"].ConvertInvariant<decimal>(),
						QuoteToSettle = array["quoteToSettleMultiplier"].ConvertInvariant<decimal>() /
						array["multiplier"].ConvertInvariant<decimal>(),
						Volume = new ExchangeVolume
						{
							QuoteCurrencyVolume = array["volume"].ConvertInvariant<decimal>() * array["lastPrice"].ConvertInvariant<decimal>(),
							QuoteCurrency = market.QuoteCurrency,
							BaseCurrencyVolume = array["volume"].ConvertInvariant<decimal>(),
							BaseCurrency = market.BaseCurrency,
							Timestamp = now
						}
					})) ;
				}
			}
			return tickers;
		}

		private async Task<ExchangeTicker> ParseTickerAsync(string symbol, JToken token)
		{
			// {"priceChange":"-0.00192300","priceChangePercent":"-4.735","weightedAvgPrice":"0.03980955","prevClosePrice":"0.04056700","lastPrice":"0.03869000","lastQty":"0.69300000","bidPrice":"0.03858500","bidQty":"38.35000000","askPrice":"0.03869000","askQty":"31.90700000","openPrice":"0.04061300","highPrice":"0.04081900","lowPrice":"0.03842000","volume":"128015.84300000","quoteVolume":"5096.25362239","openTime":1512403353766,"closeTime":1512489753766,"firstId":4793094,"lastId":4921546,"count":128453}
			return await this.ParseTickerAsync(token, symbol, "askPrice", "bidPrice", "midPrice", "volume", "quoteVolume", "closeTime", TimestampType.UnixMilliseconds);
		}

		private async Task<ExchangeTicker> ParseTickerWebSocketAsync(JToken token)
		{
			string marketSymbol = token["s"].ToStringInvariant();
			return await this.ParseTickerAsync(token, marketSymbol, "a", "b", "c", "v", "q", "E", TimestampType.UnixMilliseconds);
		}

		protected override async Task<IEnumerable<MarketCandle>> OnGetCandlesAsync(string marketSymbol, int periodSeconds, DateTime? startDate = null, DateTime? endDate = null, int? limit = null)
		{
			/*
			 [
{"timestamp":"2017-01-01T00:00:00.000Z","symbol":"XBTUSD","open":968.29,"high":968.29,"low":968.29,"close":968.29,"trades":0,"volume":0,"vwap":null,"lastSize":null,"turnover":0,"homeNotional":0,"foreignNotional":0},
{"timestamp":"2017-01-01T00:01:00.000Z","symbol":"XBTUSD","open":968.29,"high":968.76,"low":968.49,"close":968.7,"trades":17,"volume":12993,"vwap":968.72,"lastSize":2000,"turnover":1341256747,"homeNotional":13.412567469999997,"foreignNotional":12993},
			 */

			List<MarketCandle> candles = new List<MarketCandle>();
			string periodString = PeriodSecondsToString(periodSeconds);
			string url = $"/trade/bucketed?binSize={periodString}&partial=false&symbol={marketSymbol}&reverse=true" + marketSymbol;
			if (startDate != null)
			{
				url += "&startTime=" + startDate.Value.ToString("yyyy-MM-ddTHH:mm:ss");
			}
			if (endDate != null)
			{
				url += "&endTime=" + endDate.Value.ToString("yyyy-MM-ddTHH:mm:ss");
			}
			if (limit != null)
			{
				url += "&count=" + (limit.Value.ToStringInvariant());
			}

			var obj = await MakeJsonRequestAsync<JToken>(url);
			foreach (var t in obj)
			{
				candles.Add(this.ParseCandle(t, marketSymbol, periodSeconds, "open", "high", "low", "close", "timestamp", TimestampType.Iso8601, "volume", "turnover", "vwap"));
			}
			candles.Reverse();

			return candles;
		}

		public async Task<IEnumerable<ExchangeTrade>> GetHistoricalTradesAsync(
			string marketSymbol = null,
			DateTime? startDate = null,
			DateTime? endDate = null,
			int? startingIndex = null,
			int? limit = 1000)
		{
			List<ExchangeTrade> trades = new List<ExchangeTrade>();
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			string url = "/trade?";
			url += "&columns=[\"symbol\", \"size\", \"price\", \"side\", \"timestamp\", \"trdMatchID\"]";
			if (!string.IsNullOrWhiteSpace(marketSymbol))
			{
				url += "&symbol=" + NormalizeMarketSymbol(marketSymbol);
			}
			if (startDate != null)
			{
				url += "&startTime=" + startDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
			}
			if (endDate != null)
			{
				url += "&endTime=" + endDate.Value.ToString("yyyy-MM-ddTHH:mm:ss.fff") + "Z";
			}
			if (limit != null)
			{
				url += "&count=" + (limit.Value.ToStringInvariant());
			}
			if (startingIndex != null)
			{
				url += "&start=" + (startingIndex.Value.ToStringInvariant());
			}

			var obj = await MakeJsonRequestAsync<JToken>(url);
			foreach (var t in obj)
			{
				trades.Add(t.ParseTrade("size", "price", "side", "timestamp", TimestampType.Iso8601, "trdMatchID"));
			}

			return trades;
		}

		protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
		{
			


			Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
			var payload = await GetNoncePayloadAsync();
			JToken token = await MakeJsonRequestAsync<JToken>($"/user/margin?currency=all", BaseUrl, payload);
			foreach (var item in token)
			{
				var balance = item["walletBalance"].ConvertInvariant<decimal>();
				var currency = item["currency"].ToStringInvariant();
				if (currency =="XBt")
				{
					balance /= 100000000m;
					currency = currency.Replace("t", "T");
				}
				if (currency == "USDt")
				{
					balance /= 1000000m;
					currency = currency.Replace("t", "T");
				}

				if (amounts.ContainsKey(currency))
				{
					amounts[currency] += balance;
				}
				else
				{
					amounts[currency] = balance;
				}
			}

			return amounts;
		}

		protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
		{
			Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
			var payload = await GetNoncePayloadAsync();
			JToken token = await MakeJsonRequestAsync<JToken>($"/user/margin?currency=all", BaseUrl, payload);
			foreach (var item in token)
			{
				var balance = item["availableMargin"].ConvertInvariant<decimal>();
				var currency = item["currency"].ToStringInvariant();

				if (amounts.ContainsKey(currency))
				{
					amounts[currency] += balance;
				}
				else
				{
					amounts[currency] = balance;
				}
			}
			return amounts;
		}

		public async Task<IEnumerable<ExchangePosition>> GetCurrentPositionsAsync()
		{
			var payload = await GetNoncePayloadAsync();
			string url = "/position?";
			url += "&columns=[\"symbol\", \"currentQty\", \"avgEntryPrice\", \"liquidationPrice\", \"leverage\", \"lastPrice\", \"currentTimestamp\"]";
			JToken token = await MakeJsonRequestAsync<JToken>(url, BaseUrl, payload);
			List<ExchangePosition> positions = new List<ExchangePosition>();
			foreach (var item in token)
			{
				positions.Add(ParsePosition(item));
			}
			return positions;
		}

		protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string marketSymbol = null)
		{
			List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			//string query = "/order";
			string query = "/order?filter={\"open\": true}";
			if (!string.IsNullOrWhiteSpace(marketSymbol))
			{
				query += "&symbol=" + NormalizeMarketSymbol(marketSymbol);
			}
			JToken token = await MakeJsonRequestAsync<JToken>(query, BaseUrl, payload, "GET");
			foreach (JToken order in token)
			{
				orders.Add(ParseOrder(order));
			}

			return orders;
		}

		protected override async Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string marketSymbol = null, bool isClientOrderId = false)
		{
			List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			string query = $"/order?filter={{\"{(isClientOrderId ? "clOrdID" : "orderID")}\": \"{orderId}\"}}";
			JToken token = await MakeJsonRequestAsync<JToken>(query, BaseUrl, payload, "GET");
			foreach (JToken order in token)
			{
				orders.Add(ParseOrder(order));
			}

			return orders[0];
		}

		protected override async Task OnCancelOrderAsync(string orderId, string marketSymbol = null)
		{
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			payload["orderID"] = orderId;
			JToken token = await MakeJsonRequestAsync<JToken>("/order", BaseUrl, payload, "DELETE");
		}
	
		public async Task CancelAllOrdersAsync(string marketSymbol = null)
		{
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			string query = "/order/all";
			if (!string.IsNullOrWhiteSpace(marketSymbol))
			{
				payload["symbol"] = NormalizeMarketSymbol(marketSymbol);
			}
			JToken token = await MakeJsonRequestAsync<JToken>(query, BaseUrl, payload, "DELETE");
		}

		public async Task DeadmanAsync(int timeoutMS)
		{
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			payload["timeout"] = timeoutMS;
			JToken token = await MakeJsonRequestAsync<JToken>("/order/cancelAllAfter", BaseUrl, payload, "POST");
		}
		
		protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
		{
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			AddOrderToPayload(order, payload);
			JToken token = await MakeJsonRequestAsync<JToken>("/order", BaseUrl, payload, "POST");
			return ParseOrder(token);
		}

		private async Task<ExchangeOrderResult[]> PlaceOrdersAsync(string requestMethod, params ExchangeOrderRequest[] orders)
		{
			List<ExchangeOrderResult> results = new List<ExchangeOrderResult>();
			Dictionary<string, object> payload = await GetNoncePayloadAsync();
			List<Dictionary<string, object>> orderRequests = new List<Dictionary<string, object>>();
			foreach (ExchangeOrderRequest order in orders)
			{
				Dictionary<string, object> subPayload = new Dictionary<string, object>();
				AddOrderToPayload(order, subPayload);
				orderRequests.Add(subPayload);
			}
			payload["orders"] = orderRequests;
			JToken token = await MakeJsonRequestAsync<JToken>("/order/bulk", BaseUrl, payload, requestMethod);
			foreach (JToken orderResultToken in token)
			{
				results.Add(ParseOrder(orderResultToken));
			}
			return results.ToArray();
		}

		protected override async Task<ExchangeOrderResult[]> OnPlaceOrdersAsync(params ExchangeOrderRequest[] orders)
		{
			return await PlaceOrdersAsync("POST", orders);
		}

		public async Task<ExchangeOrderResult[]> AmendOrdersAsync(params ExchangeOrderRequest[] orders)
		{
			return await PlaceOrdersAsync("PUT", orders);
		}

		private void AddOrderToPayload(ExchangeOrderRequest order, Dictionary<string, object> payload)
		{
			payload["symbol"] = order.MarketSymbol;
			payload["ordType"] = order.OrderType.ToStringInvariant();
			payload["side"] = order.IsBuy ? "Buy" : "Sell";
			payload["orderQty"] = order.Amount;

			if(order.OrderId != null)
				payload["orderID"] = order.OrderId;

			if(order.ClientOrderId != null)
				payload["clOrdID"] = order.ClientOrderId;

			if(order.OrderType!=OrderType.Market)
				payload["price"] = order.Price;

			if (order.IsPostOnly == true)
				payload["execInst"] = "ParticipateDoNotInitiate"; // Also known as a Post-Only order. If this order would have executed on placement, it will cancel instead. This is intended to protect you from the far touch moving towards you while the order is in transit. It is not intended for speculating on the far touch moving away after submission - we consider such behaviour abusive and monitor for it.

			order.ExtraParameters.CopyTo(payload);
		}

		private ExchangePosition ParsePosition(JToken token)
		{
			ExchangePosition result = new ExchangePosition
			{
				MarketSymbol = token["symbol"].ToStringUpperInvariant(),
				Amount = token["currentQty"].ConvertInvariant<decimal>(),
				AveragePrice = token["avgEntryPrice"].ConvertInvariant<decimal>(),
				LiquidationPrice = token["liquidationPrice"].ConvertInvariant<decimal>(),
				Leverage = token["leverage"].ConvertInvariant<decimal>(),
				LastPrice = token["lastPrice"].ConvertInvariant<decimal>(),
				TimeStamp = CryptoUtility.ParseTimestamp(token["currentTimestamp"], TimestampType.Iso8601)
			};
			return result;
		}

		private ExchangeOrderResult ParseOrder(JToken token)
		{
			/*
{[
  {
	"orderID": "b7b8518a-c0d8-028d-bb6e-d843f8f723a3",
	"clOrdID": "",
	"clOrdLinkID": "",
	"account": 93592,
	"symbol": "XBTUSD",
	"side": "Buy",
	"simpleOrderQty": null,
	"orderQty": 1,
	"price": 5500,
	"displayQty": null,
	"stopPx": null,
	"pegOffsetValue": null,
	"pegPriceType": "",
	"currency": "USD",
	"settlCurrency": "XBt",
	"ordType": "Limit",
	"timeInForce": "GoodTillCancel",
	"execInst": "ParticipateDoNotInitiate",
	"contingencyType": "",
	"exDestination": "XBME",
	"ordStatus": "Canceled",
	"triggered": "",
	"workingIndicator": false,
	"ordRejReason": "",
	"simpleLeavesQty": 0,
	"leavesQty": 0,
	"simpleCumQty": 0,
	"cumQty": 0,
	"avgPx": null,
	"multiLegReportingType": "SingleSecurity",
	"text": "Canceled: Canceled via API.\nSubmission from testnet.bitmex.com",
	"transactTime": "2018-07-08T09:20:39.428Z",
	"timestamp": "2018-07-08T11:35:05.334Z"
  }
]}
			*/
			ExchangeOrderResult result = new ExchangeOrderResult
			{
				Amount = token["orderQty"].ConvertInvariant<decimal>(),
				AmountFilled = token["cumQty"].ConvertInvariant<decimal>(),
				Price = token["price"].ConvertInvariant<decimal>(),
				IsBuy = token["side"].ToStringInvariant().EqualsWithOption("Buy"),
				OrderDate = token["transactTime"].ConvertInvariant<DateTime>(),
				OrderId = token["orderID"].ToStringInvariant(),
				ClientOrderId = token["clOrdID"].ToStringInvariant(),
				MarketSymbol = token["symbol"].ToStringInvariant()
			};

			// http://www.onixs.biz/fix-dictionary/5.0.SP2/tagNum_39.html
			switch (token["ordStatus"].ToStringInvariant())
			{
				case "Pending New":
					result.Result = ExchangeAPIOrderResult.PendingOpen;
					break;
				case "New":
					result.Result = ExchangeAPIOrderResult.Open;
					break;
				case "PartiallyFilled":
					result.Result = ExchangeAPIOrderResult.FilledPartially;
					break;
				case "Filled":
					result.Result = ExchangeAPIOrderResult.Filled;
					break;
				case "Canceled":
					result.Result = ExchangeAPIOrderResult.Canceled;
					break;
				case "Rejected":
					result.Result = ExchangeAPIOrderResult.Rejected;
					break;
				case "Expired":
					result.Result = ExchangeAPIOrderResult.Expired;
					break;

				default:
					result.Result = ExchangeAPIOrderResult.Rejected;
					break;
			}

			return result;
		}


		//private decimal GetInstrumentTickSize(ExchangeMarket market)
		//{
		//    if (market.MarketName == "XBTUSD")
		//    {
		//        return 0.01m;
		//    }
		//    return market.PriceStepSize.Value;
		//}

		//private ExchangeMarket GetMarket(string symbol)
		//{
		//    var m = GetSymbolsMetadata();
		//    return m.Where(x => x.MarketName == symbol).First();
		//}

		//private decimal GetPriceFromID(long id, ExchangeMarket market)
		//{
		//    return ((100000000L * market.Idx) - id) * GetInstrumentTickSize(market);
		//}

		//private long GetIDFromPrice(decimal price, ExchangeMarket market)
		//{
		//    return (long)((100000000L * market.Idx) - (price / GetInstrumentTickSize(market)));
		//}
	}

	public partial class ExchangeName { public const string BitMEX = "BitMEX"; }
}
