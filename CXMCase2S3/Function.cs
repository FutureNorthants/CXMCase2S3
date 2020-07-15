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
        private static String taskToken;
        private static Boolean live = false;
        private Secrets secrets = null;


        public async Task FunctionHandler(object input, ILambdaContext context)
        {
            if (await GetSecrets())
            {
                String instance = Environment.GetEnvironmentVariable("instance");
                JObject o = JObject.Parse(input.ToString());
                caseReference = (string)o.SelectToken("CaseReference");
                taskToken = (string)o.SelectToken("TaskToken");
                Console.WriteLine("caseReference : " + caseReference);
                switch (instance.ToLower())
                {
                    case "live":
                        live = true;
                        String caseDetailsLive = await GetCaseDetailsAsync(secrets.cxmEndPointLive, secrets.cxmAPIKeyLive);
                        await SaveCase(caseDetailsLive);
                        await SendSuccessAsync();
                        break;
                    case "test":
                        String caseDetailsTest = await GetCaseDetailsAsync(secrets.cxmEndPointTest, secrets.cxmAPIKeyTest);
                        await SaveCase(caseDetailsTest);
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
                    caseDetails = responseContent.ReadAsStringAsync().Result;
                }
                else
                {
                    await SendFailureAsync("Getting case details for " + caseReference, response.StatusCode.ToString(), taskToken);
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + request.ToString());
                    Console.WriteLine("ERROR : GetStaffResponseAsync : " + response.StatusCode.ToString());
                }
            }
            catch (Exception error)
            {
                await SendFailureAsync("Getting case details for " + caseReference, error.Message, taskToken);
                Console.WriteLine("ERROR : GetStaffResponseAsync : " + error.StackTrace);
            }
            return caseDetails;
        }

        private async Task<Boolean> SaveCase(String caseDetails)
        {
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
                        Key = caseReference,
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
            public string cxmEndPointTest { get; set; }
            public string cxmEndPointLive { get; set; }
            public string cxmAPIKeyTest { get; set; }
            public string cxmAPIKeyLive { get; set; }
        }
    }
}
