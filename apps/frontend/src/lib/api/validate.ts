import { z, type ZodType } from 'zod';

export const MAX_BODY_BYTES = 1_048_576;

export class ValidationError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ValidationError';
  }
}

function assertBodySize(req: Request) {
  const len = req.headers.get('content-length');
  if (len && Number(len) > MAX_BODY_BYTES) {
    throw new ValidationError('Request body too large');
  }
}

export async function parseBody<T>(req: Request, schema: ZodType<T>): Promise<T> {
  assertBodySize(req);
  let json: unknown;
  try {
    json = await req.json();
  } catch {
    throw new ValidationError('Invalid JSON body');
  }
  const result = schema.safeParse(json);
  if (!result.success) {
    const msg = result.error.errors.map((e) => e.message).join('; ');
    throw new ValidationError(msg || 'Validation failed');
  }
  return result.data;
}
