export async function GET() {
  return fetch("https://api.example.test/v1/items?secret=redacted-fixture#fragment");
}

export const POST = async () => fetch("/local");
