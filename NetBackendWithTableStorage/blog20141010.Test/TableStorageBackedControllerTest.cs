﻿using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.WindowsAzure.MobileServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace blog20141010.Test
{
    [TestClass]
    public class TableStorageBackedControllerTest
    {
        const string AppUrl = "https://blog20141010.azure-mobile.net/";
        const string AppKey = "bNmUnQlSgxtzFGFnDRUljypFfHbLLa98";
        const int ItemsInTable = 100;
        const int RowNumberOffset = 1000;
        static int SumOfAges = 0;
        static HttpClient client;

        [ClassInitialize]
        public static void Initialize(TestContext context)
        {
            client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-ZUMO-APPLICATION", AppKey);

            CleanTable().Wait();
            PopulateTable().Wait();
        }

        private static async Task PopulateTable()
        {
            Random rndGen = new Random();
            for (int i = 0; i < ItemsInTable; i++)
            {
                JObject body = new JObject();
                body.Add("id", "partition,row" + (RowNumberOffset + i));
                body.Add("name", GenerateName(rndGen));
                int age = rndGen.Next(18, 80);
                body.Add("age", age);
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, AppUrl + "tables/person");
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                using (var resp = await client.SendAsync(req))
                {
                    resp.EnsureSuccessStatusCode();
                    SumOfAges += age;
                }
            }
        }

        private static async Task CleanTable()
        {
            var mobileClient = new MobileServiceClient(AppUrl, AppKey);
            var table = mobileClient.GetTable("person");
            bool hasMore = true;
            while (hasMore)
            {
                var result = await table.ReadAsync("$top=10", null, true);
                var items = (JArray)result["results"];
                foreach (var item in items)
                {
                    var id = (string)item["id"];
                    await table.DeleteAsync(new JObject(new JProperty("id", id)));
                }

                hasMore = result["nextLink"] != null;
            }
        }

        private static string GenerateName(Random rndGen)
        {
            StringBuilder sb = new StringBuilder();
            const string Vowels = "aeiouy";
            const string Consonants = "bcdfghjklmnpqrstvwxz";
            foreach (var firstName in new[] { true, false })
            {
                if (!firstName) sb.Append(' ');
                for (var i = 0; i < rndGen.Next(2, 6); i++)
                {
                    sb.Append(Consonants[rndGen.Next(Consonants.Length)]);
                    sb.Append(Vowels[rndGen.Next(Vowels.Length)]);
                }
            }

            return sb.ToString();
        }

        [TestMethod]
        public async Task TakePartial_ReturnsContinuationLink()
        {
            using (var resp = await client.GetAsync(AppUrl + "/tables/person?$top=" + (ItemsInTable / 10)))
            {
                resp.EnsureSuccessStatusCode();
                Assert.IsTrue(resp.Headers.Contains("Link"));
                var link = resp.Headers.GetValues("Link").First();
                Assert.IsTrue(link.EndsWith("; rel=next"));
            }
        }

        [TestMethod]
        public async Task ContinuationLink_CanBeUsedToRetrieveAllItems()
        {
            int itemCount = 0;
            string link = null;
            while (itemCount < ItemsInTable)
            {
                var url = link ?? (AppUrl + "tables/person?$top=" + (ItemsInTable / 10));
                using (var resp = await client.GetAsync(url))
                {
                    resp.EnsureSuccessStatusCode();
                    var respBody = await resp.Content.ReadAsStringAsync();
                    var items = JArray.Parse(respBody);
                    itemCount += items.Count;

                    if (resp.Headers.Contains("Link"))
                    {
                        link = resp.Headers.GetValues("Link").First();
                        if (link.EndsWith("; rel=next"))
                        {
                            link = link.Substring(0, link.Length - "; rel=next".Length);
                        }
                    }
                    else
                    {
                        link = null;
                    }
                }
            }

            if (link != null)
            {
                // If there is an extra link, should not return any data
                using (var resp = await client.GetAsync(link))
                {
                    resp.EnsureSuccessStatusCode();
                    var respBody = await resp.Content.ReadAsStringAsync();
                    Assert.AreEqual("[]", respBody);
                }
            }
        }

        [TestMethod]
        public async Task Lookup_DoesNotReturnContinuationLink()
        {
            var url = AppUrl + "tables/person/partition,row" + (RowNumberOffset);
            using (var resp = await client.GetAsync(url))
            {
                resp.EnsureSuccessStatusCode();
                Assert.IsFalse(resp.Headers.Contains("Link"));
                var respBody = await resp.Content.ReadAsStringAsync();
                var item = JToken.Parse(respBody);
                Assert.AreEqual(JTokenType.Object, item.Type);
            }
        }

        [TestMethod]
        public async Task ClientSDK_TypedTables_ReturnLinkHeader()
        {
            var client = new MobileServiceClient(AppUrl, AppKey);
            var table = client.GetTable<Person>();
            var items = await table.Take(ItemsInTable / 10).ToListAsync();
            var queryResult = items as IQueryResultEnumerable<Person>;
            Assert.IsNotNull(queryResult);
            Assert.IsNotNull(queryResult.NextLink);
        }

        [TestMethod]
        public async Task ClientSDK_TypedTables_CanRetrieveAllItems()
        {
            var client = new MobileServiceClient(AppUrl, AppKey);
            var table = client.GetTable<Person>();
            var items = await table.Take(ItemsInTable / 10).ToEnumerableAsync();
            var itemCount = 0;
            while (items != null)
            {
                itemCount += items.Count();
                var queryResult = items as IQueryResultEnumerable<Person>;
                Assert.IsNotNull(queryResult);
                if (queryResult.NextLink != null)
                {
                    items = await table.ReadAsync<Person>(queryResult.NextLink);
                }
                else
                {
                    items = null;
                }
            }

            Assert.AreEqual(ItemsInTable, itemCount);
        }

        [TestMethod]
        public async Task ClientSDK_UntypedTables_ReturnLinkHeader()
        {
            var client = new MobileServiceClient(AppUrl, AppKey);
            var table = client.GetTable("person");
            var items = await table.ReadAsync("$top=" + (ItemsInTable / 10), null, true);
            Assert.IsInstanceOfType(items, typeof(JObject));
            var link = items["nextLink"];
            Assert.IsNotNull(link);
        }

        [TestMethod]
        public async Task ClientSDK_TypedTables_CanRetrieveAllItems_ValidateData()
        {
            var age = await CalculateAverageAge();
            var expected = ((double)SumOfAges) / ItemsInTable;
            Assert.AreEqual(expected, age, 0.00001);
        }

        [TestMethod]
        public async Task ClientSDK_UntypedTables_CanRetrieveAllItems_ValidateData()
        {
            var age = await CalculateAverageAgeFromJsonTable();
            var expected = ((double)SumOfAges) / ItemsInTable;
            Assert.AreEqual(expected, age, 0.00001);
        }

        public async Task<double> CalculateAverageAge()
        {
            var client = new MobileServiceClient(AppUrl, AppKey);
            var table = client.GetTable<Person>();
            var sum = 0.0;
            var count = 0;
            var items = await table.Take(10).ToEnumerableAsync();
            while (items != null && items.Count() != 0)
            {
                count += items.Count();
                sum += Enumerable.Sum(items, i => i.Age);

                var queryResult = items as IQueryResultEnumerable<Person>;
                if (queryResult != null && queryResult.NextLink != null)
                {
                    items = await table.ReadAsync<Person>(queryResult.NextLink);
                }
                else
                {
                    items = null;
                }
            }

            return sum / count;
        }

        public async Task<double> CalculateAverageAgeFromJsonTable()
        {
            var client = new MobileServiceClient(AppUrl, AppKey);
            var table = client.GetTable("person");
            var sum = 0.0;
            var count = 0;
            var response = await table.ReadAsync("$top=10", null, wrapResult: true);
            while (response != null)
            {
                var items = (JArray)response["results"];
                var nextLink = (string)response["nextLink"];

                count += items.Count();
                sum += Enumerable.Sum(items, i => (int)i["age"]);

                if (nextLink != null)
                {
                    response = await table.ReadAsync(nextLink, null, true);
                }
                else
                {
                    response = null;
                }
            }

            return sum / count;
        }
    }

    public class Person
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }
        
        [JsonProperty("age")]
        public int Age { get; set; }
    }
}
