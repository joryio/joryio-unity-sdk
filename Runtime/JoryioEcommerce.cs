using System.Collections.Generic;

namespace Joryio.Runtime
{
    /// <summary>A product, as the e-commerce events describe it.</summary>
    public class EcommerceProduct
    {
        public string ProductId { get; set; }
        public string Name { get; set; }
        public double Price { get; set; }
        public int? Quantity { get; set; }
        public string Category { get; set; }
        public string Brand { get; set; }
        public string Variant { get; set; }
        public string VariantId { get; set; }
        public string Sku { get; set; }
        public string ImageUrl { get; set; }
        public string Url { get; set; }

        internal virtual Dictionary<string, object> ToPayload()
        {
            var p = new Dictionary<string, object>
            {
                { "productId", ProductId ?? string.Empty },
                { "name", Name ?? string.Empty },
                { "price", Price },
            };
            if (Quantity.HasValue) p["quantity"] = Quantity.Value;
            Put(p, "category", Category);
            Put(p, "brand", Brand);
            Put(p, "variant", Variant);
            Put(p, "variantId", VariantId);
            Put(p, "sku", Sku);
            Put(p, "imageUrl", ImageUrl);
            Put(p, "url", Url);
            return p;
        }

        internal static void Put(Dictionary<string, object> p, string key, object value)
        {
            if (value != null) p[key] = value;
        }
    }

    /// <summary>
    /// A cart line. Quantity is required and defaults to 1: a line with zero
    /// quantity has an item total of zero, which is a silently wrong revenue
    /// number rather than an obviously wrong one. Same default as the natives.
    /// </summary>
    public class EcommerceCartItem : EcommerceProduct
    {
        public new int Quantity { get; set; } = 1;

        internal override Dictionary<string, object> ToPayload()
        {
            var p = base.ToPayload();
            p["quantity"] = Quantity;
            return p;
        }
    }

    /// <summary>A completed order, for Purchase.</summary>
    public class EcommerceOrder
    {
        public string OrderId { get; set; }
        public List<EcommerceCartItem> Items { get; set; } = new List<EcommerceCartItem>();
        public double Value { get; set; }
        public string Currency { get; set; }
        public double? Shipping { get; set; }
        public double? Tax { get; set; }
        public double? Discount { get; set; }
        public string Coupon { get; set; }

        internal Dictionary<string, object> ToPayload()
        {
            var p = new Dictionary<string, object>
            {
                { "orderId", OrderId ?? string.Empty },
                { "items", JoryioEcommerce.Lines(Items) },
                { "value", Value },
            };
            EcommerceProduct.Put(p, "currency", Currency);
            if (Shipping.HasValue) p["shipping"] = Shipping.Value;
            if (Tax.HasValue) p["tax"] = Tax.Value;
            if (Discount.HasValue) p["discount"] = Discount.Value;
            EcommerceProduct.Put(p, "coupon", Coupon);
            return p;
        }
    }

    /// <summary>
    /// E-commerce events with the platform's canonical names applied for you:
    /// <c>JoryioSDK.Ecommerce.Purchase(order)</c>.
    ///
    /// Every call forwards to the native tracker, which owns the vocabulary -
    /// nothing here spells an event name. There is deliberately no alias layer
    /// server-side, so a hand-typed "Added to Cart" is not a near-miss for
    /// "Product Added": it is a different event that matches no e-commerce
    /// report and no revenue attribution. The method list and payload keys are
    /// React Native's, so one bridge contract serves both.
    ///
    /// A call missing a required argument logs a warning and sends nothing
    /// (see the guards below).
    /// </summary>
    public sealed class JoryioEcommerce
    {
        internal JoryioEcommerce() { }

        // ── Required arguments (contract item 42) ────────────────────────────
        //
        // A missing required argument warns and sends NOTHING - never an empty
        // or zero event. These used to be filled with defaults, so
        // Purchase(null) tracked an Order Completed with orderId "" and a total
        // of 0: not an error anyone would see, a plausible zero in the revenue
        // report. A NaN or infinite amount is refused the same way: JSON has no
        // spelling for it, so it crossed to the native tracker as null and was
        // recorded as 0. The same required arguments as React Native's tracker.

        public void ViewProduct(EcommerceProduct product)
        {
            if (!HasProduct("ViewProduct", product)) return;
            Send("viewProduct", new Dictionary<string, object> { { "product", product.ToPayload() } });
        }

        public void ViewCategory(string categoryId, string categoryName)
        {
            if (!HasText("ViewCategory", "categoryId", categoryId)) return;
            if (!HasText("ViewCategory", "categoryName", categoryName)) return;
            Send("viewCategory", new Dictionary<string, object> { { "categoryId", categoryId }, { "categoryName", categoryName } });
        }

        public void Search(string query, int? resultCount = null)
        {
            if (!HasText("Search", "query", query)) return;
            var p = new Dictionary<string, object> { { "query", query } };
            if (resultCount.HasValue) p["resultCount"] = resultCount.Value;
            Send("search", p);
        }

        public void AddToCart(EcommerceCartItem item, double? cartValue = null)
        {
            if (!HasProduct("AddToCart", item, "item")) return;
            if (!OptionalAmount("AddToCart", "cartValue", cartValue)) return;
            var p = new Dictionary<string, object> { { "item", item.ToPayload() } };
            if (cartValue.HasValue) p["cartValue"] = cartValue.Value;
            Send("addToCart", p);
        }

        public void RemoveFromCart(EcommerceProduct product, int quantity = 1)
        {
            if (!HasProduct("RemoveFromCart", product)) return;
            Send("removeFromCart", new Dictionary<string, object> { { "product", product.ToPayload() }, { "quantity", quantity } });
        }

        public void CartViewed(IList<EcommerceCartItem> items, double cartValue)
        {
            if (!HasCart("CartViewed", items, cartValue)) return;
            Send("cartViewed", new Dictionary<string, object> { { "items", Lines(items) }, { "cartValue", cartValue } });
        }

        public void UpdateCart(IList<EcommerceCartItem> items, double cartValue)
        {
            if (!HasCart("UpdateCart", items, cartValue)) return;
            Send("updateCart", new Dictionary<string, object> { { "items", Lines(items) }, { "cartValue", cartValue } });
        }

        public void StartCheckout(IList<EcommerceCartItem> items, double cartValue)
        {
            if (!HasCart("StartCheckout", items, cartValue)) return;
            Send("startCheckout", new Dictionary<string, object> { { "items", Lines(items) }, { "cartValue", cartValue } });
        }

        public void AddPaymentInfo(string paymentMethod)
        {
            if (!HasText("AddPaymentInfo", "paymentMethod", paymentMethod)) return;
            Send("addPaymentInfo", new Dictionary<string, object> { { "paymentMethod", paymentMethod } });
        }

        /// <summary>
        /// A completed order. OrderId, Items and a finite Value are required:
        /// the value is what revenue reports read, and an order missing any of
        /// them is ignored with a warning rather than recorded as a zero.
        /// </summary>
        public void Purchase(EcommerceOrder order)
        {
            if (order == null || string.IsNullOrWhiteSpace(order.OrderId))
            {
                Warn("Purchase needs an order with an OrderId");
                return;
            }
            if (order.Items == null)
            {
                Warn("Purchase needs an order with a list of Items");
                return;
            }
            if (!Amount("Purchase", "order.Value", order.Value)) return;
            if (!OptionalAmount("Purchase", "order.Shipping", order.Shipping)) return;
            if (!OptionalAmount("Purchase", "order.Tax", order.Tax)) return;
            if (!OptionalAmount("Purchase", "order.Discount", order.Discount)) return;
            if (!FinitePrices("Purchase", order.Items)) return;
            Send("purchase", new Dictionary<string, object> { { "order", order.ToPayload() } });
        }

        public void OrderFulfilled(string orderId, string trackingNumber = null, string carrier = null)
        {
            if (!HasText("OrderFulfilled", "orderId", orderId)) return;
            var p = new Dictionary<string, object> { { "orderId", orderId } };
            EcommerceProduct.Put(p, "trackingNumber", trackingNumber);
            EcommerceProduct.Put(p, "carrier", carrier);
            Send("orderFulfilled", p);
        }

        public void OrderDelivered(string orderId)
        {
            if (!HasText("OrderDelivered", "orderId", orderId)) return;
            Send("orderDelivered", new Dictionary<string, object> { { "orderId", orderId } });
        }

        public void OrderCancelled(string orderId, string reason = null)
        {
            if (!HasText("OrderCancelled", "orderId", orderId)) return;
            var p = new Dictionary<string, object> { { "orderId", orderId } };
            EcommerceProduct.Put(p, "reason", reason);
            Send("orderCancelled", p);
        }

        public void OrderRefunded(string orderId, double? refundAmount = null, string reason = null)
        {
            if (!HasText("OrderRefunded", "orderId", orderId)) return;
            if (!OptionalAmount("OrderRefunded", "refundAmount", refundAmount)) return;
            var p = new Dictionary<string, object> { { "orderId", orderId } };
            if (refundAmount.HasValue) p["refundAmount"] = refundAmount.Value;
            EcommerceProduct.Put(p, "reason", reason);
            Send("orderRefunded", p);
        }

        public void AddToWishlist(EcommerceProduct product)
        {
            if (!HasProduct("AddToWishlist", product)) return;
            Send("addToWishlist", new Dictionary<string, object> { { "product", product.ToPayload() } });
        }

        public void RemoveFromWishlist(EcommerceProduct product)
        {
            if (!HasProduct("RemoveFromWishlist", product)) return;
            Send("removeFromWishlist", new Dictionary<string, object> { { "product", product.ToPayload() } });
        }

        public void ShareProduct(EcommerceProduct product, string shareMethod)
        {
            if (!HasProduct("ShareProduct", product)) return;
            if (!HasText("ShareProduct", "shareMethod", shareMethod)) return;
            Send("shareProduct", new Dictionary<string, object> { { "product", product.ToPayload() }, { "shareMethod", shareMethod } });
        }

        public void ApplyCoupon(string couponCode, double? discountAmount = null, string discountType = null)
        {
            if (!HasText("ApplyCoupon", "couponCode", couponCode)) return;
            if (!OptionalAmount("ApplyCoupon", "discountAmount", discountAmount)) return;
            var p = new Dictionary<string, object> { { "couponCode", couponCode } };
            if (discountAmount.HasValue) p["discountAmount"] = discountAmount.Value;
            EcommerceProduct.Put(p, "discountType", discountType);
            Send("applyCoupon", p);
        }

        public void RemoveCoupon(string couponCode)
        {
            if (!HasText("RemoveCoupon", "couponCode", couponCode)) return;
            Send("removeCoupon", new Dictionary<string, object> { { "couponCode", couponCode } });
        }

        public void SubmitReview(string productId, int rating, string reviewText = null)
        {
            if (!HasText("SubmitReview", "productId", productId)) return;
            var p = new Dictionary<string, object> { { "productId", productId }, { "rating", rating } };
            EcommerceProduct.Put(p, "reviewText", reviewText);
            Send("submitReview", p);
        }

        private static void Warn(string what) =>
            UnityEngine.Debug.LogWarning($"[Joryio] Ecommerce.{what}; ignored.");

        // ONE set on every Joryio SDK and wrapper (pinned by the backend's
        // ecommerce-guard-parity spec). An id that is only whitespace counts as
        // missing, as in the native trackers.
        private static bool HasText(string caller, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) return true;
            Warn($"{caller} needs a non-empty {name}");
            return false;
        }

        /// <summary>A product with a ProductId and a finite Price.</summary>
        private static bool HasProduct(string caller, EcommerceProduct product, string name = "product")
        {
            if (product == null || string.IsNullOrWhiteSpace(product.ProductId))
            {
                Warn($"{caller} needs {(name == "item" ? "an" : "a")} {name} with a ProductId");
                return false;
            }
            return Amount(caller, name + ".Price", product.Price);
        }

        private static bool HasCart(string caller, IList<EcommerceCartItem> items, double cartValue)
        {
            if (items == null)
            {
                Warn($"{caller} needs a list of items");
                return false;
            }
            return Amount(caller, "cartValue", cartValue) && FinitePrices(caller, items);
        }

        private static bool Amount(string caller, string name, double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value)) return true;
            Warn($"{caller} needs a finite number for {name}, not {value}");
            return false;
        }

        /// <summary>Absent is fine; present must be finite.</summary>
        private static bool OptionalAmount(string caller, string name, double? value) =>
            !value.HasValue || Amount(caller, name, value.Value);

        /// <summary>Every line a ProductId and a finite price (a null line is skipped by Lines).</summary>
        private static bool FinitePrices(string caller, IEnumerable<EcommerceCartItem> items)
        {
            foreach (var item in items)
            {
                if (item != null && !HasProduct(caller, item, "item")) return false;
            }
            return true;
        }

        internal static List<object> Lines(IEnumerable<EcommerceCartItem> items)
        {
            var lines = new List<object>();
            if (items == null) return lines;
            foreach (var item in items)
            {
                if (item != null) lines.Add(item.ToPayload());
            }
            return lines;
        }

        private static void Send(string method, Dictionary<string, object> payload) =>
            JoryioSDK.SendEcommerce(method, payload);
    }
}
