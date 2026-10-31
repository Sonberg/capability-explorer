import { DefaultAzureCredential } from "@azure/identity";
import { ServiceBusClient } from "@azure/service-bus";
import config from "../../config/default.json" with { type: "json" };
import { saveSignal } from "../db/priceRepository.js";

type Handler = (body: unknown) => Promise<void>;

async function onStatsUpdated(body: unknown): Promise<void> {
  const message = body as { ArticleId: string; AveragePerWeek: number };
  await saveSignal(message.ArticleId, message.AveragePerWeek);
}

const handlers: Record<string, Handler> = {
  ArticleStatsUpdated: onStatsUpdated,
};

export async function startStatsListener(): Promise<void> {
  const client = new ServiceBusClient(config.serviceBus.fullyQualifiedNamespace, new DefaultAzureCredential());
  const receiver = client.createReceiver(config.serviceBus.queue);
  receiver.subscribe({
    processMessage: async (message) => {
      const handler = handlers[String(message.subject)];
      if (handler) {
        await handler(message.body);
      }
    },
    processError: async (args) => {
      console.error(args.error);
    },
  });
}
