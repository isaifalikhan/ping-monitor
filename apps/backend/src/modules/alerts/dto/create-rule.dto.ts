import { ApiProperty, ApiPropertyOptional } from '@nestjs/swagger';
import { IsEnum, IsString, IsOptional, IsUUID } from 'class-validator';
import { AlertTrigger } from '@prisma/client';

export class CreateAlertRuleDto {
  @ApiProperty()
  @IsUUID()
  channelId: string;

  @ApiProperty({ enum: AlertTrigger })
  @IsEnum(AlertTrigger)
  trigger: AlertTrigger;

  @ApiPropertyOptional()
  @IsOptional()
  @IsUUID()
  monitorId?: string;
}
