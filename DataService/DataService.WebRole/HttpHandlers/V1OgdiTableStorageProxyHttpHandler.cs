﻿using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json;
using Ogdi.Azure.Data;
using Ogdi.Config;
using System;
using Ogdi.Azure;
using System.Collections.Specialized;

namespace Ogdi.DataServices
{
    public class V1OgdiTableStorageProxyHttpHandler : TableStorageHttpHandlerBase, IHttpHandler
    {        

        private HttpContext _context;
        private string _afdPublicServiceReplacementUrl;
        private string _azureTableUrlToReplace;
        private string _entityKind;

        public string AzureTableRequestEntityUrl { get; set; }
        public string OgdiAlias { get; set; }
        public string EntitySet { get; set; }
        
        public bool IsAvailableEndpointsRequest { get; set; }

        #region IHttpHandler Members

        public bool IsReusable
        {
            get { return true; }
        }

        public void ProcessRequest(HttpContext context)
        {
            _entityKind = null;

            if (!this.IsHttpGet(context))
            {
                this.RespondForbidden(context);
            }
            else
            {
                _context = context;
                WebRequest request;

                var account = (IsAvailableEndpointsRequest) ? AppSettings.Account 
                                                            : AppSettings.ParseStorageAccount(  AppSettings.EnabledStorageAccounts[OgdiAlias].storageaccountname,
                                                                                                AppSettings.EnabledStorageAccounts[OgdiAlias].storageaccountkey);

                // <tconte>
                // Modifications pour sécuriser les accès aux flux DPMA
                // On laisse passer les requêtes vers les tables de métadonnées
                // Le reste est intercepté et on implémente une authentification HTTP Basic

                if (this.OgdiAlias == "DPMA"
                    && this.EntitySet != "TableMetadata"
                    && this.EntitySet != "EntityMetadata"
                    && this.EntitySet != "ProcessorParams"
                    && this.EntitySet != "TableColumnsMetadata")
                {
                    if (_context.Request.Headers["Authorization"] == null)
                    {
                        _context.Response.StatusCode = 401;
                        _context.Response.StatusDescription = "Access Denied";
                        _context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Secure DPMA feeds\"";
                        _context.Response.End();
                        return;
                    }
                    else
                    {
                        string credsHeader = _context.Request.Headers["Authorization"];
                        string creds = null;

                        int credsPosition = credsHeader.IndexOf("Basic", StringComparison.OrdinalIgnoreCase);

                        if (credsPosition != -1)
                        {
                            credsPosition += "Basic".Length + 1;
                            creds = credsHeader.Substring(credsPosition, credsHeader.Length - credsPosition);
                        }

                        string user = ASCIIEncoding.ASCII.GetString(Convert.FromBase64String(creds)).Split(':')[0];

                        if (user != "dpmauser")
                        {
                            _context.Response.StatusCode = 401;
                            _context.Response.StatusDescription = "Access Denied";
                            _context.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Secure DPMA feeds\"";
                            _context.Response.End();
                            return;
                        }
                    }
                }

                // </tconte>

                request = CreateTableStorageSignedRequest(context, account, AzureTableRequestEntityUrl, IsAvailableEndpointsRequest);

                Action<string,string,string> incView = AnalyticsRepository.RegisterView;
                incView.BeginInvoke(String.Format("{0}||{1}", OgdiAlias, EntitySet), 
                    context.Request.RawUrl,
                    context.Request.UserHostName,
                    null, null);

                try
                {
                    var response = request.GetResponse();
                    var responseStream = response.GetResponseStream();
                    var feed = XElement.Load(XmlReader.Create(responseStream));

                    _context.Response.Headers["DataServiceVersion"] = "2.0;";
                    _context.Response.CacheControl = "no-cache";
                    _context.Response.AddHeader("x-ms-request-id", response.Headers["x-ms-request-id"]);

                    string continuationNextPartitionKey = response.Headers["x-ms-continuation-NextPartitionKey"];
                    string continuationNextRowKey = response.Headers["x-ms-continuation-NextRowKey"];

                    string formatContinuationLink = null;
                    if (continuationNextPartitionKey != null && continuationNextRowKey != null)
                    {
                        _context.Response.AddHeader("x-ms-continuation-NextPartitionKey", continuationNextPartitionKey);
                        _context.Response.AddHeader("x-ms-continuation-NextRowKey", continuationNextRowKey);

                        formatContinuationLink = GenerateSkipTokenContinuationUrl(_context, continuationNextPartitionKey, continuationNextRowKey);              
                    }

                    string format = !string.IsNullOrEmpty(_context.Request.QueryString["$format"]) ? _context.Request.QueryString["$format"] : _context.Request.QueryString["format"];
                    
                    SetupReplacementUrls();

                    switch (format)
                    {
                        case "kml":
                            RenderKml(feed);
                            break;
                        case "json":
                            RenderJson(feed);
                            break;
                        case "rdf":
                            RenderRDF(feed);
                            break;
                        default:
                            // If "format" is not kml or json, then assume AtomPub
                            RenderAtomPub(feed, formatContinuationLink);
                            break;
                    }
                }
                catch (WebException ex)
                {
                    throw ex;
                    var response = ex.Response as HttpWebResponse;
                    _context.Response.StatusCode = (int)response.StatusCode;
                    _context.Response.End();
                }
            }
        }
        
        private string LoadEntityKind(HttpContext context, string entitySet)
        {
            var requestUrl = AppSettings.TableStorageBaseUrl + "TableMetadata";
            
            WebRequest request = CreateTableStorageSignedRequest(context,
                                                                 AppSettings.ParseStorageAccount(
                                                                    AppSettings.EnabledStorageAccounts[OgdiAlias].storageaccountname,
                                                                    AppSettings.EnabledStorageAccounts[OgdiAlias].storageaccountkey),
                                                                 requestUrl, false, true);

            try
            {
                var response = request.GetResponse();
                if (response != null)
                {
                    var responseStream = response.GetResponseStream();
                    if (responseStream != null)
                    {
                        var feed = XElement.Load(XmlReader.Create(responseStream));

                        var propertiesElements = feed.Elements(_nsAtom + "entry").Elements(_nsAtom + "content").Elements(_nsm + "properties");

                        foreach (var e in propertiesElements)
                        {
                            if (e == null) continue;

                            if (entitySet.Equals(e.Element(_nsd + "entityset").Value, StringComparison.InvariantCultureIgnoreCase))
                                return e.Element(_nsd + "entitykind").Value;
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.End();
            }
            return null;
        }

        private string GenerateSkipTokenContinuationUrl(HttpContext context, string continuationNextPartitionKey, string continuationNextRowKey)
        {
            string formatContinuationLink = null;

            var scheme = context.Request.Url.Scheme;
            var host = context.Request.Url.Host;
            var port = context.Request.Url.Port;
            var path = context.Request.Path;
            var qryString = context.Request.QueryString;

            if (qryString.Count == 0)
            {
                formatContinuationLink = scheme + "://" + host + ":" + port + path + "?" + "$skiptoken='" + context.Server.UrlEncode("&") + "NextPartitionKey=" + continuationNextPartitionKey + context.Server.UrlEncode("&") + "NextRowKey=" + continuationNextRowKey + "'";
            }
            else
            {
                NameValueCollection newQueryString = new NameValueCollection();
                foreach (var key in qryString.AllKeys)
                {
                    if (key != "$skiptoken")
                    {
                        newQueryString.Add(key, qryString[key]);
                    }
                }
                var azureTableRequestUrlBuilder = new StringBuilder();
                foreach (var key in newQueryString.AllKeys)
                {
                    if (newQueryString[key] != "")
                    {
                        azureTableRequestUrlBuilder.Append(context.Server.HtmlEncode("&"));
                        azureTableRequestUrlBuilder.Append(key);
                        azureTableRequestUrlBuilder.Append("=");
                        azureTableRequestUrlBuilder.Append(newQueryString[key]);
                    }
                    
                }
                formatContinuationLink = scheme + "://" + host + ":" + port + path + "?" + azureTableRequestUrlBuilder.ToString() + context.Server.HtmlEncode("&") +"$skiptoken='" + _context.Server.UrlEncode("&") + "NextPartitionKey=" + continuationNextPartitionKey + _context.Server.UrlEncode("&") + "NextRowKey=" + continuationNextRowKey + "'";
            }
            return formatContinuationLink;
        }

        #endregion

        private void RenderKml(XElement feed)
        {
            const string STARTING_KML = "<kml xmlns=\"http://www.opengis.net/kml/2.2\"><Document><name></name>";
            const string ENDING_KML = "</Document></kml>";
            
            _context.Response.ContentType = "application/vnd.google-earth.kml+xml";

            _context.Response.Write(STARTING_KML);

            var propertiesElements = GetPropertiesElements(feed);

            foreach (var propertiesElement in propertiesElements)
            {
                var kmlSnippet = propertiesElement.Element(_nsd + "kmlsnippet");
                propertiesElement.Elements(_rdfSnippetXName).Remove();

                if (kmlSnippet != null)
                {
                    // If the kmlsnippet size is <= 64K, then we just store
                    // it in the <kmlsnippet/> element.  However, due to the
                    // 64K string storage limitations in Azure Tables,
                    // we store larger KML snippets in a Azure Blob.
                    // In this case the <kmlsnippet/> element contains:
                    //      <KmlSnippetReference><Container>zipcodes</Container><Blob>33a8d702-c21b-4b09-8cdb-a09cef2e3115</Blob></KmlSnippetReference>
                    // We need to parse this string into an XElement and then
                    // go get the kml snippet out of the blob.
                    // From a perf perspective, this is not ideal.  
                    // However, "it is what it is."

                    var kmlSnippetValue = kmlSnippet.Value;

                    if (kmlSnippetValue.Contains("KmlSnippetReference"))
                    {
                        var blobId = XElement.Parse(kmlSnippetValue).Element("Blob").Value;
                        var request = CreateBlobStorageSignedRequest(blobId, OgdiAlias, EntitySet);
                        var response = request.GetResponse();
                        var strReader = new StreamReader(response.GetResponseStream());
                        var kmlSnippetString = strReader.ReadToEnd();

                        _context.Response.Write(kmlSnippetString);
                    }
                    else
                    {
                        _context.Response.Write(kmlSnippetValue);
                    }
                }
            }

            _context.Response.Write(ENDING_KML);
        }

        private void RenderRDF(XElement feed)
        {
            XDocument doc = new XDocument(new XDeclaration("1.0", "utf-8", ""));

            _context.Response.AddHeader("content-disposition", "attachment; filename=" + EntitySet + ".rdf");
            _context.Response.ContentType = "application/rss+xml";
            _context.Response.Charset = "UTF-8";
            _context.Response.ContentEncoding = System.Text.Encoding.UTF8;

            XElement rdfNamespaces = getRdfNamespaces(EntitySet);
            XElement rdfMetadataValue;

            var propertiesElements = GetPropertiesElements(feed);

            XNamespace rdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

            string ogdiUrl = string.Empty;
            string baseUrl = string.Empty;
            string beginUrl = string.Empty;
            string endUrl = string.Empty;

            foreach (var propertiesElement in propertiesElements)
            {
                string rdfSnip = propertiesElement.Element(_nsd + "rdfsnippet").ToString();

                if (rdfSnip.Contains("ogdiUrl") == true)
                {
                    baseUrl = _context.Request.Url.AbsoluteUri.Split('?')[0];
                    beginUrl = baseUrl.Substring(0, baseUrl.IndexOf("v1") + 3);
                    endUrl = baseUrl.Substring(baseUrl.IndexOf("v1") + 3);
                    ogdiUrl = beginUrl + "ColumnsMetadata/" + endUrl;
                    rdfSnip = rdfSnip.Replace("ogdiUrl", ogdiUrl);
                }
                XElement rdfSnippet = XElement.Parse(rdfSnip);

                if (rdfSnippet != null)
                {
                    var rdfSnippetValue = rdfSnippet.Value;

                    if (rdfSnippetValue.Contains("RdfSnippetReference"))
                    {
                        var blobId = XElement.Parse(rdfSnippetValue).Element("Blob").Value;
                        var request = this.CreateBlobStorageSignedRequest(blobId, this.OgdiAlias, this.EntitySet);
                        var response = request.GetResponse();
                        var strReader = new StreamReader(response.GetResponseStream());
                        var rdfSnippetString = strReader.ReadToEnd();

                        rdfNamespaces.Add(XElement.Parse(rdfSnippetValue).Element(rdfNamespace + "Description"));
                    }
                    else
                    {
                        rdfMetadataValue = XElement.Parse(rdfSnippetValue).Element(rdfNamespace + "Description");
                        rdfNamespaces.Add(rdfMetadataValue);
                    }
                }
            }
            doc.Add(rdfNamespaces);
            doc.Save(_context.Response.Output);
            _context.Response.End();
        }

        private string SerializeAnObject(object obj)
        {
            System.Xml.XmlDocument doc = new XmlDocument();
            System.Xml.Serialization.XmlSerializer serializer = new System.Xml.Serialization.XmlSerializer(obj.GetType());
            System.IO.MemoryStream stream = new System.IO.MemoryStream();

            try
            {
                serializer.Serialize(stream, obj);
                stream.Position = 0;
                doc.Load(stream);
                return doc.InnerXml;
            }
            catch
            {
                throw;
            }
            finally
            {
                stream.Close();
                stream.Dispose();
            }
        }

        private void RenderJson(XElement feed)
        {
            _context.Response.ContentType = "application/json";
            XName kmlSnippetElementString = _nsd + "kmlsnippet";

            var sb = new StringBuilder();
            var sw = new StringWriter(sb);

            using (JsonWriter jsonWriter = new JsonTextWriter(sw))
            {
                jsonWriter.WriteStartObject();
                jsonWriter.WritePropertyName("d");
                jsonWriter.WriteStartArray();

                IEnumerable<XElement> propertiesElements = GetPropertiesElements(feed);

                foreach (var propertiesElement in propertiesElements)
                {
                    jsonWriter.WriteStartObject();

                    propertiesElement.Elements(kmlSnippetElementString).Remove();
                    propertiesElement.Elements(_rdfSnippetXName).Remove();

                    foreach (var element in propertiesElement.Elements())
                    {
                        jsonWriter.WritePropertyName(element.Name.LocalName);
                        jsonWriter.WriteValue(element.Value);
                    }

                    jsonWriter.WriteEndObject();
                }

                jsonWriter.WriteEndArray();
                jsonWriter.WriteEndObject();
            }

            var callbackFunctionName = _context.Request["callback"];

            if (callbackFunctionName != null)
            {
                _context.Response.Write(callbackFunctionName);
                _context.Response.Write("(");
                _context.Response.Write(sb.ToString());
                _context.Response.Write(");");
            }
            else
            {
                _context.Response.Write(sb.ToString());
            }
        }

        private void RenderAtomPub(XElement feed, string formatContinuationLink)
        {   
            // Update Azure Table Storage url for //feed/id
            string idValue = feed.Element(_idXName).Value;
            string baseValue = feed.Attribute(XNamespace.Xml + "base").Value;

            feed.Attribute(XNamespace.Xml + "base").Value = ReplaceAzureUrlInString(baseValue);

            feed.Element(_idXName).Value = ReplaceAzureUrlInString(idValue);
            
            // The xml payload coming back has a <kmlsnippet> property.  We want to
            // hide that from the consumer of our service by removing it.
            // NOTE: We only use kmlsnippet when returning KML.

            // Iterate through all the entries to update 
            // Azure Table Storage url for //feed/entry/id
            // and remove kmlsnippet element from all instances of
            // //feed/entry/content/properties
            
            IEnumerable<XElement> entries = feed.Elements(_entryXName);

            //bool isSingleEntry = true;

            foreach (var entry in entries)
            {
                idValue = entry.Element(_idXName).Value;
                entry.Element(_idXName).Value = ReplaceAzureUrlInString(idValue);

                ReplaceAzureNamespaceInCategoryTermValue(entry);

                var properties = entry.Elements(_contentXName).Elements(_propertiesXName);

                if (!this.IsAvailableEndpointsRequest)
                {
                    properties.Elements(_kmlSnippetXName).Remove();
                    properties.Elements(_rdfSnippetXName).Remove();
                }
                else 
                {
                    properties.Elements(_storageAccountNameXName).Remove();
                    properties.Elements(_storageAccountKeyXName).Remove();
                }
            }

            _context.Response.ContentType = "application/atom+xml;charset=utf-8";

            if (formatContinuationLink != null)
            {
                string str = feed.ToString();
                int index = str.IndexOf("</feed");
                str = str.Substring(0, index);
                str = str + "<link rel=\"next\" href=\"" + formatContinuationLink + "\" />" + "</feed>";
                _context.Response.Write(str.ToString());

            }
            else
            {
                _context.Response.Write(feed.ToString());
            }
        }

        private void ReplaceAzureNamespaceInCategoryTermValue(XElement entry)
        {
            // use the simple approach of representing "entitykind" as
            // "entityset" value plus the text "Item."  A decision was made to do
            // this at the service level for now so that we wouldn't have to deal 
            // with changing the data import code and the existing values in the 
            // EntityMetadata table.

            var term = entry.Element(_categoryXName).Attribute("term");

            //TODO: apply real fix. OgdiAlias is null for AvailableEndpoints
            if (OgdiAlias == null) return;
            if (_entityKind == null)
            {
                var termValue = term.Value;
                var dotLocation = termValue.ToString().IndexOf(".");
                var entitySet = termValue.Substring(dotLocation + 1);
                _entityKind = LoadEntityKind(_context, entitySet);
            }
            term.Value = string.Format(_termNamespaceString, OgdiAlias.ToLower(), _entityKind);
        }

        private void SetupReplacementUrls()
        {
            // The xml payload returned from Table Storage data service has urls
            // that point back to Table Storage.  We need to replace the urls with the
            // proper urls for our public service.
            var sb = new StringBuilder(_context.Request.Url.Scheme); 
            sb.Append("://"); 
            sb.Append(_context.Request.Url.Host); 
            sb.Append("/v1/");            

            if (!IsAvailableEndpointsRequest)
            {
                sb.Append(OgdiAlias);
                sb.Append("/");

                _azureTableUrlToReplace = string.Format(AppSettings.TableStorageBaseUrl,
                                                    AppSettings.EnabledStorageAccounts[OgdiAlias].storageaccountname);
            }
            else
            {
                _azureTableUrlToReplace = string.Format(AppSettings.TableStorageBaseUrl,
                                                    AppSettings.OgdiConfigTableStorageAccountName);
            }

            _afdPublicServiceReplacementUrl = sb.ToString();
        }

        private string ReplaceAzureUrlInString(string xmlString)
        {
            // The xml payload returned from Table Storage data service has urls
            // that point back to Table Storage.  We need to replace the urls with the
            // proper urls for our public service.
            return xmlString.Replace(_azureTableUrlToReplace, _afdPublicServiceReplacementUrl);
        }

        private static IEnumerable<XElement> GetPropertiesElements(XElement feed)
        {
            return feed.Elements(_entryXName).Elements(_contentXName).Elements(_propertiesXName);
        }

        #region RDF TableColumnsMetadata
        private XElement getRdfNamespaces(string entitySet)
        {
            List<string> ns = new List<string>();

            XNamespace rdfNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

            XElement rdfXml = new XElement(rdfNamespace + "RDF",
            new XAttribute(XNamespace.Xmlns + "rdf", rdfNamespace.ToString()));
            string customNamespaceUrl = string.Empty;
            string baseUrl = string.Empty;
            string beginUrl = string.Empty;
            string endUrl = string.Empty;

            if (entitySet != null)
            {
                ns = TableStorageHttpHandlerBase.GetTableColumnsMetadata(entitySet, OgdiAlias);

                if (ns.Count > 0)
                {
                    foreach (string item in ns)
                    {
                        if (item == "ogdi=ogdiUrl")
                        {
                            baseUrl = _context.Request.Url.AbsoluteUri.Split('?')[0];
                            beginUrl = baseUrl.Substring(0, baseUrl.IndexOf("v1") + 3);
                            endUrl = baseUrl.Substring(baseUrl.IndexOf("v1") + 3);
                            customNamespaceUrl = beginUrl + "ColumnsMetadata/" + endUrl;
                        }
                        else
                            customNamespaceUrl = item.Split('=')[1];

                        rdfXml.Add(new XAttribute(XNamespace.Xmlns + item.Split('=')[0], customNamespaceUrl));
                    }
                }
                return rdfXml;
            }
            else
            {
                return rdfXml;
            }

        }
        #endregion
    }
}