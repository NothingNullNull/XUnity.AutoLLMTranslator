using System;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Www;

internal class LLMTranslatorEndpoint : WwwEndpoint
{

    #region Since all batching and concurrency are handled within TranslatorTask, please do not modify these two parameters.
    public override int MaxTranslationsPerRequest => 1;
    public override int MaxConcurrency => 100;

    #endregion

    public override string Id => "AutoLLMTranslate";

    public override string FriendlyName => "AutoLLM Translate";
    TranslatorTask task = new TranslatorTask();

    public override void Initialize(IInitializationContext context)
    {
        try
        {
            var debuglvl = context.GetOrCreateSetting<Logger.LogLevel>("AutoLLM", "LogLevel", Logger.LogLevel.Error);
            var log2file = context.GetOrCreateSetting<bool>("AutoLLM", "Log2File", false);
            Logger.InitLogger(debuglvl, log2file);
        }
        catch (Exception ex)
        {            
            Logger.InitLogger(Logger.LogLevel.Error, false);
            Logger.Error($"{ex}");
        }
        context.SetTranslationDelay(0.1f);
        task.Init(context);
    }

    public override void OnCreateRequest(IWwwRequestCreationContext context)
    {
        Logger.Debug($"翻译请求: {context.UntranslatedTexts[0]}");
        context.Complete(new WwwRequestInfo("http://127.0.0.1:20000/", SimpleJson.SerializeTexts(context.UntranslatedTexts)));
    }

    public override void OnExtractTranslation(IWwwTranslationExtractionContext context)
    {
        var data = context.ResponseData;

        Logger.Debug($"翻译结果: {data}");
        var rs = SimpleJson.ParseTexts(data);
        if ((rs?.Length ?? 0) == 0)
        {
            context.Fail("翻译结果为空");
        }
        else
            context.Complete(rs);
    }

}