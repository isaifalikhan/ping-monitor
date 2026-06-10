import { createParamDecorator, ExecutionContext } from '@nestjs/common';
import { JwtPayload } from '@netwatch/shared';

export const OrgId = createParamDecorator(
  (_data: unknown, ctx: ExecutionContext): string => {
    const request = ctx.switchToHttp().getRequest();
    return (request.user as JwtPayload).organizationId;
  },
);
