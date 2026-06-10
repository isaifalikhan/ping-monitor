import { ApiProperty, ApiPropertyOptional } from '@nestjs/swagger';
import { IsEnum, IsString, IsObject, IsOptional, MaxLength } from 'class-validator';
import { AlertChannel } from '@prisma/client';

export class CreateAlertChannelDto {
  @ApiProperty({ enum: AlertChannel })
  @IsEnum(AlertChannel)
  channel: AlertChannel;

  @ApiProperty()
  @IsString()
  @MaxLength(100)
  name: string;

  @ApiPropertyOptional()
  @IsOptional()
  @IsObject()
  config?: Record<string, unknown>;
}
