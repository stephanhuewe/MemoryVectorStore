using MemoryVectorDB;
using DataChunker;
using System.Text;
using Mistral.SDK.DTOs;
using Mistral.SDK;
using static System.Net.Mime.MediaTypeNames;
using System.Reflection;

namespace MemoryVectorDB_sample
{

    // todo: add sample code interpreting the chunks
    internal class Program
    {
        static async Task Main(string[] args)
        {
            string OpenAIkey           = File.Exists("apikey.txt") ?File.ReadAllText("apikey.txt") :"";    // "API key here"; // OpenAI key
            string RepoURL = "";
            string gitPath = "C:\\temp\\gittest\\";                              // Code Base

            //string queryString = $"Create unit tests for ChunkGenerator.cs"; // string to look for // WORKS
            //string queryString = $"Explain the functionality of ChunkGenerator.cs"; // string to look for
            //string queryString = $"Do a code review on ChunkGenerator"; // string to look for

            Console.WriteLine("Welcome to ADC Code Chatbot, please enter your question.");
            string queryString = Console.ReadLine();

            Console.WriteLine("** Starting embedding code");

            var embeddingSample = new EmbeddingSample(OpenAIkey);
            // Create embedding, only needed once

            foreach (var file in Directory.GetFiles(gitPath, "*.cs"))
            {
                Console.WriteLine("** Embedding document");
                string documentVectorsPath = $"{file}.json";                             

                if (File.Exists(documentVectorsPath))
                {
                    Console.WriteLine("** Vectors already exist, reading previous embedding");
                    // Read embedding
                    embeddingSample.DeserializeDocumentText(file);
                    await embeddingSample.DeserializeVectorsAsync(documentVectorsPath);
                }
                else
                {
                    await embeddingSample.WordEmbeddingAsync(file);
                    embeddingSample.SerializeDocumentText(file);

                    await embeddingSample.SerializeVectorsAsync(documentVectorsPath);
                }
            }
            //}
         
            var findings = await embeddingSample.GetFindingsAsync(queryString);

            // Todo: have OpenAI interpret the embeded chunks
            Console.WriteLine("** Answer");
            await embeddingSample.FormulateAnswerAsync(queryString,findings);

            Console.WriteLine("** Done");
            Console.WriteLine("Please enter 'r' for a new question. ");

            string restart = Console.ReadLine();
           
            if (restart.ToUpper() == "R")
            {                
                System.Diagnostics.Process.Start(System.AppDomain.CurrentDomain.FriendlyName);
                Environment.Exit(0);
            }
        }



        public class EmbeddingSample
        {
            private VectorDB<Chunk>         _vectorCollection;
            private MistralClient           _mistralService;
            private Dictionary<string, Document>?          _documents;
            private ChunkGenerator?         _chunkGenerator;

            public EmbeddingSample(string apiKey)
            {
                //  Mistral service that we are going to use for embedding                   
                _mistralService = new MistralClient(new APIAuthentication(apiKey));

                // Collection of vectors made of chunks of document
                _vectorCollection = new VectorDB<Chunk>(100, ChunkEmbedingAsync);
                                
                _documents = new Dictionary<string, Document>();
            }

            public async Task WordEmbeddingAsync(string documentPath)
            {

                // Document to embed
                var document = new Document();
                document.Add(File.ReadAllText(documentPath), documentPath);

                // Chunk generator
                _chunkGenerator = new ChunkGenerator(200, 100, document);

                var i = 0;
                // Get the chunks and embed them
                foreach (var chunk in _chunkGenerator.GetChunk())
                {
                    Console.WriteLine($"***Chunk {i++}***");
                    Console.WriteLine(chunk.Text);

                    // Add the source reference
                    chunk.Source = documentPath;

                    // Embed the chunk
                    await _vectorCollection.AddAsync(chunk);

                    // We clean out the text, to safe memory: we just need the vector, start index and length
                    chunk.Text = null!;
                }

                _documents.Add(documentPath,document);
            }
            public async Task SerializeVectorsAsync(string fileName)
            {
                await _vectorCollection.SerializeJsonAsync(fileName);        
            }

            public void SerializeDocumentText(string documentTextPath)
            {
                // Write the document to disk
                if (_documents[documentTextPath] == null) return;
                File.WriteAllText(documentTextPath, _documents[documentTextPath].Text);
            }

            internal void DeserializeDocumentText(string documentTextPath)
            {
                var document = new Document();
                document.Add(File.ReadAllText(documentTextPath),"");
                document.Source = documentTextPath;
                _documents.Add(documentTextPath, document);
            }


            internal async Task DeserializeVectorsAsync(string fileName)
            {
                await _vectorCollection.DeserializeJsonAsync(fileName);
            }

            // Callback function for embedding in the vector database
            private async Task<Chunk?> ChunkEmbedingAsync(Chunk inputObject)
            {
                var request = new EmbeddingRequest(
                    ModelDefinitions.MistralEmbed,
                    new List<string> { inputObject.Text },
                    EmbeddingRequest.EncodingFormatEnum.Float);
                EmbeddingResponse response = await _mistralService.Embeddings.GetEmbeddingsAsync(request);
                               

                if (response != null)
                {
                    var value = response.Data.FirstOrDefault()?.Embedding;
                    if (value==null) return null!;
                    inputObject.SetVector(value);
                    return inputObject;
                }
                else { return null!; }                    
            }

            public async Task FormulateAnswerAsync(string query, SortedList<float, Chunk> bestMatches)
            {
                StringBuilder queryBuilder = new StringBuilder();  
                
                // Basic format of the query:
                queryBuilder.AppendLine($"Answer the following query {query}. Only use the content below to construct the answer, use the file names as reference. If no content is shown below or if it is not applicable, answer: \"Sorry, I have no data on that\" \n\n");
                
                // Insert the best matches
                foreach (var match in bestMatches)
                {
                    var chunk = match.Value;
                    queryBuilder.AppendLine($"file name {chunk.SourceIndex}:");
                    queryBuilder.AppendLine(_documents[chunk.Source].Text.Substring(chunk.StartCharNo, chunk.CharLength)+"\n" ?? "");
                }

                
                
                var request = new ChatCompletionRequest(
                    //define model - required
                    ModelDefinitions.MistralMedium,
                    //define messages - required
                    new List<Mistral.SDK.DTOs.ChatMessage>()
                            {
                            new Mistral.SDK.DTOs.ChatMessage(Mistral.SDK.DTOs.ChatMessage.RoleEnum.System,"Your are an AI assistant. The assistant is helpful, factual and friendly."),
                            new Mistral.SDK.DTOs.ChatMessage(Mistral.SDK.DTOs.ChatMessage.RoleEnum.User,queryBuilder.ToString()),
                            },
                                //optional - defaults to false
                                safePrompt: true,
                                //optional - defaults to 0.7
                                temperature: 0,
                                //optional - defaults to null
                                maxTokens: 500,
                                //optional - defaults to 1
                                topP: 1,
                                //optional - defaults to null
                                randomSeed: 32);


                // OpenAI
                //// Ask Completion to answer the query
                //var completionResult = await _openAiService.ChatCompletion.CreateCompletion(new ChatCompletionCreateRequest
                //{
                //    Messages = new List<ChatMessage>
                //    {
                //        ChatMessage.FromSystem("Your are an AI assistant. The assistant is helpful, factual and friendly."), 
                //        ChatMessage.FromUser(queryBuilder.ToString()),
                //    },
                //    Model = Models.Gpt_3_5_Turbo,
                //});

                //// Show the answer
                //if (completionResult.Successful)
                //{
                //    Console.WriteLine(completionResult.Choices.First().Message.Content);
                //}

                var response = await _mistralService.Completions.GetCompletionAsync(request);
                Console.WriteLine(response.Choices.First().Message.Content);
            }


            public async Task<SortedList<float, Chunk>> GetFindingsAsync(string query)
            {
                var querychunk  = await ChunkEmbedingAsync(new Chunk() { Text = query });  
                var queryVector = querychunk?.GetVector()??new float[0];
                var bestMatches = _vectorCollection.FindNearestSorted(queryVector, 10);

                foreach (var item in bestMatches)
                {
                    ShowMatch(item.Value, queryVector, item.Value.Source);                    
                }
                return bestMatches;
            }

            private void ShowMatch(Chunk chunk, float[] queryVector, string documentTextPath)
            {
                // Show the match if the text with the query and the text itself
                var dotProduct = DotProduct(chunk.GetVector(), queryVector);
                Console.WriteLine($"Match: {dotProduct} - {chunk.StartCharNo} - {chunk.CharLength}");
                Console.WriteLine(_documents[documentTextPath]?.Text.Substring(chunk.StartCharNo, chunk.CharLength)??"");
                Console.WriteLine();
                Console.WriteLine();
            }

            public static float DotProduct(float[] a, float[] b)
            {
                float sum = 0;
                for (int i = 0; i < a.Length; i++)
                {
                    sum += a[i] * b[i];
                }

                return sum;
            }
        }

    }
}