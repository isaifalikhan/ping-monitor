import { NextResponse } from 'next/server';
import type { ApiResponse } from '@netwatch/shared';

export function ok<T>(data: T, status = 200) {
  const body: ApiResponse<T> = { success: true, data };
  return NextResponse.json(body, { status });
}

export function fail(error: string, status = 400) {
  const body: ApiResponse = { success: false, error };
  return NextResponse.json(body, { status });
}
