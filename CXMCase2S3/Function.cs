using System.Collections.Generic;
using Amazon.Lambda.Core;
using System.Diagnostics;
using System;
using Amazon;
using System.Threading.Tasks;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.S3.Model;
using Amazon.S3;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace CXMCase2S3
{
    [DebuggerDisplay("Reference : {myTest}")]

    public class Function
    {
        private static readonly RegionEndpoint primaryRegion = RegionEndpoint.EUWest2;
        private static readonly String secretName = "nbcGlobal";
        private static readonly String secretAlias = "AWSCURRENT";

        private static String caseReference;
        private static String transition;
        private static String transitioner;
        private static String taskToken;
        private static Boolean live = false;
        private Secrets secrets = null;


        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                JObject o = JObject.Parse(input.ToString());
                caseReference = (string)o.SelectToken("CaseReference");
                transition = (string)o.SelectToken("Transition");
                transitioner = (string)o.SelectToken("Transitioner");
                taskToken = (string)o.SelectToken("TaskToken");
                String fileName = "";
                String instance = "test";
                try
                {
                    if (context.InvokedFunctionArn.ToLower().Contains("prod"))
                    {
                        instance = "live";
                    }
                }
                catch (Exception)
                {
                }
                try
                {
                    switch (transition.ToLower())
                    {
                        case "created":
                            fileName = caseReference + "-CREATED";
                            break;
                        case "awaiting-review":
                            fileName = caseReference + "-AWAITING-REVIEW";
                            break;
                        case "awaiting-customer":
                            fileName = caseReference + "-AWAITING-CUSTOMER";
                            break;
                        case "close-case":
                            fileName = caseReference + "-CLOSE-CASE";
                            break;
                        case "forward-via-email":
                            fileName = caseReference + "-FORWARD";
                            break;
                        default:
                            fileName = caseReference + "-UNDEFINED";
                            break;
                    }
                }
                catch
                {
                    await SendFailureAsync("Unexpected transition for " + caseReference, transition.ToLower(), taskToken);
                    Console.WriteLine("ERROR : GetCaseDetailsAsync : Unexpected transition : " + transition.ToLower());
                }
                switch (instance.ToLower())
                {
                    case "live":
                        live = true;
                        String caseDetailsLive = await GetCaseDetailsAsync(secrets.cxmEndPointLive, secrets.cxmAPIKeyLive);
                        await SaveCase(fileName,caseDetailsLive);
                        await SendSuccessAsync();
                        break;
                    case "test":
                        String caseDetailsTest = await GetCaseDetailsAsync(secrets.cxmEndPointTest, secrets.cxmAPIKeyTest);
                        await SaveCase(fileName,caseDetailsTest);
                        await SendSuccessAsync();
                        break;
                    default:
                        await SendFailureAsync("Instance not Live or Test : " + instance.ToLower(), "Lambda Parameter Error", taskToken);
                        Console.WriteLine("ERROR : Instance not Live or Test : " + instance.ToLower());
                        break;
                }
            }
        }

        private async Task<Boolean> GetSecrets()
        {
            IAmazonSecretsManager client = new AmazonSecretsManagerClient(primaryRegion);

            GetSecretValueRequest request = new GetSecretValueRequest();
            request.SecretId = secretName;
            request.VersionStage = secretAlias;

            try
            {
                GetSecretValueResponse response = await client.GetSecretValueAsync(request);
                secrets = JsonConvert.DeserializeObject<Secrets>(response.SecretString);
                return true;
            }
            catch (Exception error)
            {
                await SendFailureAsync("GetSecrets", error.Message, taskToken);
                Console.WriteLine("ERROR : GetSecretValue : " + error.Message);
                Console.WriteLine("ERROR : GetSecretValue : " + error.StackTrace);
                return false;
            }
        }

        private async Task<String> GetCaseDetailsAsync(String cxmEndPoint, String cxmAPIKey)
        {
            String caseDetails = "";
            HttpClient cxmClient = new HttpClient();
            cxmClient.BaseAddress = new Uri(cxmEndPoint);
            String requestParameters = "key=" + cxmAPIKey;
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "/api/service-api/norbert/case/" + caseReference + "?" + requestParameters);
            try
            {
                HttpResponseMessage response = cxmClient.SendAsync(request).Result;

                if (response.IsSuccessStatusCode)
                {
                    HttpContent responseContent = response.Content;
                    JObject caseContent = JObject.Parse(await response.Content.ReadAsStringAsync());
                    try
                    {
                        switch (transition.ToLower())
                        {
                            case "created":
                                caseDetails = responseContent.ReadAsStringAsync().Result;
                                break;
                            case "awaiting-review":
                                AwaitingReview awaitingReview = new AwaitingReview
                                {
                                    ActionDate = DateTime.Now.ToString("yyyy/MM/dd"),
                                    CaseReference = caseReference,
                                    UserEmail = transitioner
                                };
                                caseDetails = JsonConvert.SerializeObject(awaitingReview);
                                break;
                            case "awaiting-customer":
                                AwaitingCustomer awaitingCustomer = new AwaitingCustomer
                                {
                                    ActionDate = DateTime.Now.ToString("yyyy/MM/dd"),
                                    CaseReference = caseReference,
                                    UserEmail = transitioner
                                };
                                caseDetails = JsonConvert.SerializeObject(awaitingCustomer);
                                break;
                            case "close-case":
                                CloseCase closeCase = new CloseCase
                                {
                                    ActionDate = DateTime.Now.ToString("yyyy/MM/dd"),
                                    CaseReference = caseReference,
                                    UserEmail = transitioner
                                };
                                caseDetails = JsonConvert.SerializeObject(closeCase);
                                break;
                            case "forward-via-email":
                                ForwardCase forwardCase = new ForwardCase
                                {
                                    ActionDate = DateTime.Now.ToString("yyyy/MM/dd"),
                                    CaseReference = caseReference,
                                    UserEmail = transitioner,
                                    ToEmail = (String)caseContent["values"]["forward_email_to"]
                                };
                                caseDetails = JsonConvert.SerializeObject(forwardCase);
                                break;
                            default:
                                await SendFailureAsync("Unexpected transition for " + caseReference, transition.ToLower(), taskToken);
                                Console.WriteLine("ERROR : GetCaseDetailsAsync : Unexpected transition : " + transition.ToLower());
                                break;
                        }
                    }
                    catch 
                    {
                        await SendFailureAsync("Unexpected transition for " + caseReference, transition.ToLower(), taskToken);
                        Console.WriteLine("ERROR : GetCaseDetailsAsync : Unexpected transition : " + transition.ToLower());
                    }
                }
                else
                {
                    await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString(), taskToken);
                    Console.WriteLine("ERROR : GetCaseDetailsAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetCaseDetailsAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Getting case details for " + caseReference, error.Message, taskToken);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<Boolean> SaveCase(String fileName, String caseDetails)
        {
            caseDetails = caseDetails.Replace("values", "case_details");
            AmazonS3Client client = new AmazonS3Client(primaryRegion);
            try
            {
                String bucketName = "";
                if (live)
                {
                    bucketName = "nbc-reporting";
                }
                else
                {
                    bucketName = "nbc-reporting";
                }
   
                    PutObjectRequest putRequest = new PutObjectRequest()
                    { 
                        BucketName = bucketName,
                        Key = fileName,
                        ContentBody = caseDetails
                    };
                    await client.PutObjectAsync(putRequest);
            }
            catch (Exception error)
            {
                await SendFailureAsync("Saving case details for " + caseReference, error.Message, taskToken);
                Console.WriteLine("ERROR : SaveCase : " + error.StackTrace);
            }
            return true;
        }

        private async Task SendSuccessAsync()
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskSuccessRequest successRequest = new SendTaskSuccessRequest();
            successRequest.TaskToken = taskToken;
            Dictionary<String, String> result = new Dictionary<String, String>
            {
                { "Result"  , "Success"  },
                { "Message" , "Completed"}
            };

            string requestOutput = JsonConvert.SerializeObject(result, Formatting.Indented);
            successRequest.Output = requestOutput;
            try
            {
                await client.SendTaskSuccessAsync(successRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.Message);
                Console.WriteLine("ERROR : SendSuccessAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        private async Task SendFailureAsync(String failureCause, String failureError, String taskToken)
        {
            AmazonStepFunctionsClient client = new AmazonStepFunctionsClient();
            SendTaskFailureRequest failureRequest = new SendTaskFailureRequest();
            failureRequest.Cause = failureCause;
            failureRequest.Error = failureError;
            failureRequest.TaskToken = taskToken;

            try
            {
                await client.SendTaskFailureAsync(failureRequest);
            }
            catch (Exception error)
            {
                Console.WriteLine("ERROR : SendFailureAsync : " + error.Message);
                Console.WriteLine("ERROR : SendFailureAsync : " + error.StackTrace);
            }
            await Task.CompletedTask;
        }

        public class Secrets
        {
            public String cxmEndPointTest { get; set; }
            public String cxmEndPointLive { get; set; }
            public String cxmAPIKeyTest { get; set; }
            public String cxmAPIKeyLive { get; set; }
        }

        public class AwaitingReview
        {
            public String Action = "awaiting-review";
            public String ActionDate { get; set; }
            public String CaseReference { get; set; }
            public String UserEmail { get; set; }
        }

        public class AwaitingCustomer
        {
            public String Action = "awaiting-customer";
            public String ActionDate { get; set; }
            public String CaseReference { get; set; }
            public String UserEmail { get; set; }
        }

        public class CloseCase
        {
            public String Action = "close";
            public String ActionDate { get; set; }
            public String CaseReference { get; set; }
            public String UserEmail { get; set; }
        }

        public class ForwardCase
        {
            public String Action = "forward";
            public String ActionDate { get; set; }
            public String CaseReference { get; set; }
            public String UserEmail { get; set; }
            public String ToEmail { get; set; }
        }
    }
}
